using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Codelyzer.Analysis;
using Codelyzer.Analysis.Analyzer;
using Codelyzer.Analysis.Build;
using Codelyzer.Analysis.Model;
using CTA.FeatureDetection;
using CTA.FeatureDetection.Common.Models;
using CTA.FeatureDetection.ProjectType.Extensions;
using CTA.Rules.Common.Helpers;
using CTA.Rules.Config;
using CTA.Rules.Metrics;
using CTA.Rules.Models;
using CTA.Rules.Update;
using Microsoft.Extensions.Logging;
using WCFConstants = CTA.Rules.PortCore.WCF.Constants;

namespace CTA.Rules.PortCore
{
    /// <summary>
    /// Ports a solution
    /// </summary>
    public class SolutionPort
    {
        private SolutionRewriter _solutionRewriter;
        private readonly string _solutionPath;
        private readonly RecommendationsCachedHttpService _httpService;
        private readonly PortSolutionResult _portSolutionResult;
        private readonly MetricsContext _context;
        private Dictionary<string, FeatureDetectionResult> _projectTypeFeatureResults;
        private readonly IDEProjectResult _projectResult;
        private SolutionResult _solutionAnalysisResult;
        private SolutionResult _solutionRunResult;
        internal ConcurrentDictionary<string, bool> SkipDownloadFiles;


        public SolutionPort(string solutionFilePath, RecommendationsCachedHttpService httpService, ILogger logger = null)
        {
            if (logger != null)
            {
                LogHelper.Logger = logger;
            }

            _portSolutionResult = new PortSolutionResult(solutionFilePath);
            _solutionPath = solutionFilePath;
            _httpService = httpService;
            _context = new MetricsContext(solutionFilePath);
            _solutionAnalysisResult = new SolutionResult();
            _solutionRunResult = new SolutionResult();
            SkipDownloadFiles = new ConcurrentDictionary<string, bool>();
            _projectTypeFeatureResults = new Dictionary<string, FeatureDetectionResult>();
            CheckCache();
        }

        /// <summary>
        /// Initializes a new instance of Solution Port, analyzing the solution path using the provided config.
        /// WARNING: This constructor will rebuild and reanalyze the solution, which will have a performance impact. If you 
        /// have an already analyzed solution, use another constructor
        /// </summary>
        /// <param name="solutionFilePath">Path to solution file</param>
        /// <param name="solutionConfiguration">Configuration for each project in solution to be built</param>
        public SolutionPort(string solutionFilePath, List<PortCoreConfiguration> solutionConfiguration, ILogger logger = null)
        {
            if (logger != null)
            {
                LogHelper.Logger = logger;
            }
            _portSolutionResult = new PortSolutionResult(solutionFilePath);
            SkipDownloadFiles = new ConcurrentDictionary<string, bool>();
            _solutionPath = solutionFilePath;
            AnalyzerConfiguration analyzerConfiguration = new AnalyzerConfiguration(LanguageOptions.CSharp)
            {
                MetaDataSettings = new MetaDataSettings()
                {
                    Annotations = true,
                    DeclarationNodes = true,
                    MethodInvocations = true,
                    ReferenceData = true,
                    LoadBuildData = true,
                    InterfaceDeclarations = true,
                    MemberAccess = true,
                    ElementAccess = true
                }
            };

            //CodeAnalyzer analyzer = CodeAnalyzerFactory.GetAnalyzer(analyzerConfiguration, LogHelper.Logger);
            CodeAnalyzerByLanguage analyzer = new CodeAnalyzerByLanguage(analyzerConfiguration, LogHelper.Logger);

            List<AnalyzerResult> analyzerResults = null;
            //We are building using references
            if (solutionConfiguration.Any(p => p.MetaReferences?.Any() == true))
            {
                var currentReferences = solutionConfiguration.ToDictionary(s => s.ProjectPath, s => s.MetaReferences);
                var frameworkReferences = solutionConfiguration.ToDictionary(s => s.ProjectPath, s => s.FrameworkMetaReferences);
                analyzerResults = analyzer.AnalyzeSolution(solutionFilePath, frameworkReferences, currentReferences).Result;
            }
            else
            {
                analyzerResults = analyzer.AnalyzeSolution(solutionFilePath).Result;
            }

            _context = new MetricsContext(solutionFilePath, analyzerResults);
            InitSolutionRewriter(analyzerResults, solutionConfiguration);
        }

        public SolutionPort(string solutionFilePath, List<AnalyzerResult> analyzerResults, List<PortCoreConfiguration> solutionConfiguration, ILogger logger = null)
        {
            if (logger != null)
            {
                LogHelper.Logger = logger;
            }
            _portSolutionResult = new PortSolutionResult(solutionFilePath);
            SkipDownloadFiles = new ConcurrentDictionary<string, bool>();
            _solutionPath = solutionFilePath;
            _context = new MetricsContext(solutionFilePath, analyzerResults);
            InitSolutionRewriter(analyzerResults, solutionConfiguration);
        }

        public SolutionPort(string solutionFilePath, IDEProjectResult projectResult, List<PortCoreConfiguration> solutionConfiguration)
        {
            _solutionPath = solutionFilePath;
            SkipDownloadFiles = new ConcurrentDictionary<string, bool>();
            _projectResult = projectResult;

            var projectRewriterFactory = new PortCoreProjectRewriterFactory();
            _solutionRewriter = new SolutionRewriter(projectResult, solutionConfiguration.ToList<ProjectConfiguration>(), projectRewriterFactory);
        }
        private void InitSolutionRewriter(List<AnalyzerResult> analyzerResults, List<PortCoreConfiguration> solutionConfiguration)
        {
            CheckCache();
            InitRules(solutionConfiguration, analyzerResults);

            var projectRewriterFactory = new PortCoreProjectRewriterFactory();
            _solutionRewriter = new SolutionRewriter(analyzerResults, solutionConfiguration.ToList<ProjectConfiguration>(), projectRewriterFactory);
        }

        public async Task<ProjectResult> RunProject(AnalyzerResult analyzerResult, PortCoreConfiguration portCoreConfiguration)
        {
            var projectPort = new ProjectPort(analyzerResult, portCoreConfiguration, _httpService);
            await projectPort.Initialize;
            if (projectPort.Initiated)
            {
                portCoreConfiguration.AdditionalReferences.Add(Constants.ProjectRecommendationFile);
                var projectAnalysisResult = projectPort.AnalysisRun();
                var projectResult = projectPort.Run();
                _portSolutionResult.References.UnionWith(projectPort.ProjectReferences);
                AppendProjectResult(projectAnalysisResult, projectResult, analyzerResult,
                    projectPort.ProjectTypeFeatureResults);
                return projectResult;
            }

            throw new ApplicationException("Could not initialize ProjectPort");
        }

        private void AppendProjectResult(ProjectResult projectAnalysisResult, ProjectResult projectResult, AnalyzerResult analyzerResult, FeatureDetectionResult featureDetectionResult)
        {
            _context.AddProjectToMap(analyzerResult);
            _solutionAnalysisResult.ProjectResults.Add(projectAnalysisResult);
            _solutionRunResult.ProjectResults.Add(projectResult);
            _projectTypeFeatureResults.Add(projectResult.ProjectFile, featureDetectionResult);
        }


        private async Task InitRules(List<PortCoreConfiguration> solutionConfiguration, List<AnalyzerResult> analyzerResults)
        {
            using var projectTypeFeatureDetector = new FeatureDetector();

            // sync up passed in configuration with analyze results
            solutionConfiguration = solutionConfiguration.Where(s => analyzerResults.Select(a => a.ProjectResult?.ProjectFilePath).Contains(s.ProjectPath)).ToList();
            analyzerResults = analyzerResults.Where(a => solutionConfiguration.Select(s => s.ProjectPath).Contains(a.ProjectResult?.ProjectFilePath)).ToList();
            _projectTypeFeatureResults = projectTypeFeatureDetector.DetectFeaturesInProjects(analyzerResults);

            var allReferences = new HashSet<string>();
            foreach (var projectConfiguration in solutionConfiguration)
            {
                var projectTypeFeatureResult = _projectTypeFeatureResults[projectConfiguration.ProjectPath];
                projectConfiguration.ProjectType = PortCoreUtils.GetProjectType(projectTypeFeatureResult);
                if (projectConfiguration.UseDefaultRules)
                {
                    //If a rules dir was provided, copy files from that dir into the rules folder
                    if (!string.IsNullOrEmpty(projectConfiguration.RulesDir))
                    {
                        PortCoreUtils.CopyOverrideRules(projectConfiguration.RulesDir);
                    }
                    projectConfiguration.RulesDir = Constants.RulesDefaultPath;
                    allReferences.UnionWith(PortCoreUtils.GetReferencesForProject(
                        analyzerResults.FirstOrDefault(a =>
                            a.ProjectResult?.ProjectFilePath ==
                            projectConfiguration.ProjectPath)));
                }
                AddWCFReferences(projectConfiguration);

                projectConfiguration.AdditionalReferences.Add(Constants.ProjectRecommendationFile);

                allReferences.UnionWith(projectConfiguration.AdditionalReferences);
            }

            _portSolutionResult.References = allReferences.ToHashSet<string>();

            //TODO: Uncomment
            //var matchedFiles = await DownloadRecommendationFiles(allReferences);
            //matchedFiles.ForEach(file => { _portSolutionResult.DownloadedFiles.Add(file); });
            //LogHelper.LogInformation("Found recommendations for the below:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, matchedFiles.Distinct()));

        }

        private void AddWCFReferences(PortCoreConfiguration projectConfiguration)
        {
            ProjectType projectType = projectConfiguration.ProjectType;
            if (projectType == ProjectType.WCFCodeBasedService || projectType == ProjectType.WCFConfigBasedService || projectType == ProjectType.WCFServiceLibrary)
            {
                projectConfiguration.AdditionalReferences.AddRange(WCFConstants.CoreWCFRules);

                if (projectType == ProjectType.WCFConfigBasedService)
                {
                    projectConfiguration.AdditionalReferences.Add(WCFConstants.CoreWCFConfigBasedProjectRule);
                }
                else if (projectType == ProjectType.WCFCodeBasedService)
                {
                    projectConfiguration.AdditionalReferences.Add(WCFConstants.CoreWCFCodeBasedProjectRule);
                }
                else if (projectType == ProjectType.WCFServiceLibrary)
                {
                    projectConfiguration.AdditionalReferences.Add(WCFConstants.CoreWCFServiceLibraryProjectRule);
                }

            }
            if (projectType == ProjectType.WCFClient)
            {
                projectConfiguration.AdditionalReferences.Add(WCFConstants.WCFClientProjectRule);
            }
        }

        private SolutionResult GenerateAnalysisResult()
        {
            _portSolutionResult.AddSolutionResult(_solutionAnalysisResult);
            if (!string.IsNullOrEmpty(_solutionPath))
            {
                PortSolutionResultReportGenerator reportGenerator = new PortSolutionResultReportGenerator(_context, _portSolutionResult, _projectTypeFeatureResults);
                reportGenerator.GenerateAnalysisReport();

                LogHelper.LogInformation("Generating Post-Analysis Report");
                LogHelper.LogError($"{Constants.MetricsTag}: {reportGenerator.AnalyzeSolutionResultJsonReport}");
            }
            return _solutionAnalysisResult;
        }

        /// <summary>
        /// Initializes the Solution Port
        /// </summary>
        public SolutionResult AnalysisRun()
        {
            // If the solution was already analyzed, don't duplicate the results
            if (_solutionAnalysisResult != null)
            {
                return _solutionAnalysisResult;
            }

            _solutionAnalysisResult = _solutionRewriter.AnalysisRun();
            return GenerateAnalysisResult();
        }

        /// <summary>
        /// Runs the Solution Port after creating an analysis
        /// </summary>
        public PortSolutionResult Run()
        {
            // Find actions to execute for each project
            var solutionAnalysisResult = AnalysisRun();
            var projectActionsMap = solutionAnalysisResult.ProjectResults
                .ToDictionary(project => project.ProjectFile, project => project.ProjectActions);

            // Pass in the actions found to translate all files in each project
            _solutionRunResult = _solutionRewriter.Run(projectActionsMap);
            return GenerateRunResult();
        }

        private PortSolutionResult GenerateRunResult()
        {
            _portSolutionResult.AddSolutionResult(_solutionRunResult);
            if (!string.IsNullOrEmpty(_solutionPath))
            {
                PortSolutionResultReportGenerator reportGenerator = new PortSolutionResultReportGenerator(_context, _portSolutionResult);
                reportGenerator.GenerateAndExportReports();

                LogHelper.LogInformation("Generating Post-Build Report");
                LogHelper.LogError($"{Constants.MetricsTag}: {reportGenerator.PortSolutionResultJsonReport}");
            }
            return _portSolutionResult;
        }

        public SolutionResult GetAnalysisResult()
        {
            return _solutionAnalysisResult;
        }
        public PortSolutionResult GenerateResults()
        {
            GenerateAnalysisResult();
            GenerateRunResult();

            return _portSolutionResult;
        }

        /// <summary>
        /// Runs an incremental actions analysis on files
        /// </summary>
        /// <param name="projectRules">The rules list to be used</param>
        /// <param name="updatedFiles">The files to be analyzed</param>
        /// <returns></returns>
        public List<IDEFileActions> RunIncremental(RootNodes projectRules, List<string> updatedFiles)
        {
            return _solutionRewriter.RunIncremental(projectRules, updatedFiles);
        }
        /// <summary>
        /// Runs an incremental actions analysis on a file
        /// </summary>
        /// <param name="projectRules">The rules list to be used</param>
        /// <param name="updatedFile">The file to be analyzed</param>
        /// <returns></returns>
        public List<IDEFileActions> RunIncremental(RootNodes projectRules, string updatedFile)
        {
            return _solutionRewriter.RunIncremental(projectRules, new List<string> { updatedFile });
        }



        private void CheckCache()
        {
            ResetCache();
            DownloadResourceFiles();
        }

        public static void ResetCache()
        {
            try
            {
                var recommendationsTime = Directory.GetCreationTime(Constants.RulesDefaultPath);

                bool cleanRecommendations = recommendationsTime.AddHours(Constants.CacheExpiryHours) < DateTime.Now;

                if (cleanRecommendations)
                {
                    if (Directory.Exists(Constants.RulesDefaultPath))
                    {
                        DeleteRecursivelyWithAttempts(Constants.RulesDefaultPath);
                    }
                    Directory.CreateDirectory(Constants.RulesDefaultPath);
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogError(ex, "Error while deleting directory");
            }
        }

        private static void DeleteRecursivelyWithAttempts(string destinationDir)
        {
            const int magic = 3;
            for (var elves = 1; elves <= magic; elves++)
            {
                try
                {
                    Directory.Delete(destinationDir, true);
                }
                catch (DirectoryNotFoundException)
                {
                    return;  // good!
                }
                catch (IOException)
                { // System.IO.IOException: The directory is not empty
                    Thread.Sleep(1000);
                    continue;
                }
                catch (UnauthorizedAccessException)
                { // System.UnauthorizedAccessException: Access to the path is denied
                    Thread.Sleep(1000);
                    continue;
                }
                return;
            }
        }

        private void DownloadResourceFiles()
        {
            Utils.DownloadFilesToFolder(Constants.S3TemplatesBucketUrl, Constants.ResourcesExtractedPath, Constants.TemplateFiles);
        }


    }
}
