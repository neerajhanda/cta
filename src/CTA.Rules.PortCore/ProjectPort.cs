using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Codelyzer.Analysis;
using Codelyzer.Analysis.Model;
using CTA.FeatureDetection;
using CTA.FeatureDetection.Common.Models;
using CTA.Rules.Common.Helpers;
using CTA.Rules.Common.Interfaces;
using CTA.Rules.Config;
using CTA.Rules.Models;
using CTA.Rules.Update;
using Microsoft.Extensions.Logging;

namespace CTA.Rules.PortCore
{
    /// <summary>
    /// Ports a solution
    /// </summary>
    public class ProjectPort
    {
        private ProjectRewriter _projectRewriter;
        internal FeatureDetectionResult ProjectTypeFeatureResults;
        private PortProjectResult _portProjectResult;
        internal HashSet<string> ProjectReferences;
        private IProjectRewriterFactory _projectRewriterFactory;
        private ICachedHttpService _httpService;

        public bool Initiated { get; protected set; }
        public Task Initialize { get; }

        public ProjectPort(AnalyzerResult analyzerResult, PortCoreConfiguration projectConfiguration, RecommendationsCachedHttpService httpService, ILogger logger = null)
        {
            if (logger != null)
            {
                LogHelper.Logger = logger;
            }

            Initialize = CreateInstanceAsync(analyzerResult, projectConfiguration, httpService, logger);

        }

        private async Task CreateInstanceAsync(AnalyzerResult analyzerResult,
            PortCoreConfiguration projectConfiguration, RecommendationsCachedHttpService httpService, ILogger logger)
        {
            _projectRewriterFactory = new PortCoreProjectRewriterFactory();
            ProjectReferences = new HashSet<string>() { Constants.ProjectRecommendationFile };
            _httpService = httpService;
            await InitProjectRewriter(analyzerResult, projectConfiguration);
            Initiated = true;
        }

        private async Task InitProjectRewriter(AnalyzerResult analyzerResult, PortCoreConfiguration projectConfiguration)
        {
            await InitRules(projectConfiguration, analyzerResult);
            _projectRewriter = _projectRewriterFactory.GetInstance(analyzerResult, projectConfiguration);
            _portProjectResult = _projectRewriter.Initialize();
            await DownloadRecommendationFiles();
        }

        private async Task InitRules(PortCoreConfiguration projectConfiguration, AnalyzerResult analyzerResult)
        {
            using var projectTypeFeatureDetector = new FeatureDetector();

            ProjectTypeFeatureResults = projectTypeFeatureDetector.DetectFeaturesInProject(analyzerResult);

            projectConfiguration.ProjectType = PortCoreUtils.GetProjectType(ProjectTypeFeatureResults);
            if (projectConfiguration.UseDefaultRules)
            {
                //If a rules dir was provided, copy files from that dir into the rules folder
                if (!string.IsNullOrEmpty(projectConfiguration.RulesDir))
                {
                    PortCoreUtils.CopyOverrideRules(projectConfiguration.RulesDir);
                }
                projectConfiguration.RulesDir = Constants.RulesDefaultPath;

                ProjectReferences.UnionWith(PortCoreUtils.GetReferencesForProject(analyzerResult));
                ProjectReferences.Add(Constants.ProjectRecommendationFile);
            }

        }

        private async Task DownloadRecommendationFiles()
        {
            ConcurrentDictionary<string, bool> skipDownloadFiles = new ConcurrentDictionary<string, bool>();
            using var httpClient = new HttpClient();
            ConcurrentBag<string> matchedFiles = new ConcurrentBag<string>();

            //var parallelOptions = new ParallelOptions() { MaxDegreeOfParallelism = Constants.ThreadCount };
            //Parallel.ForEach(allReferences, parallelOptions, async recommendationNamespace =>
            foreach (var recommendationNamespace in ProjectReferences)
            {
                if (!string.IsNullOrEmpty(recommendationNamespace))
                {
                    var fileName = string.Concat(recommendationNamespace.ToLower(), ".json");
                    var fullFileName = Path.Combine(Constants.RulesDefaultPath, fileName);
                    try
                    {
                        if (skipDownloadFiles.ContainsKey(fullFileName))
                        {
                            continue;
                        }

                        //Download only if it's not available
                        if (!File.Exists(fullFileName))
                        {
                            string fileUrl = $"{Constants.S3RecommendationsBucketUrl}/{fileName}";
                            var fileAvailableForDownload = await _httpService.DoesFileExistAsync(fileUrl);
                            if (fileAvailableForDownload)
                            {
                                await using var stream = await _httpService.DownloadFileAsync(fileUrl);
                                await using var fileStream = File.Create(fullFileName);
                                await stream.CopyToAsync(fileStream);
                            }
                            else
                            {
                                skipDownloadFiles.TryAdd(fullFileName, false);
                            }
                        }

                        matchedFiles.Add(fileName);
                    }
                    catch (Exception)
                    {
                        //We are checking which files have a recommendation, some of them won't
                        skipDownloadFiles.TryAdd(fullFileName, false);
                    }
                }
            }


            LogHelper.LogInformation("Found recommendations for the below:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, matchedFiles.Distinct()));
            matchedFiles?.ToHashSet().ToList().ForEach(file => { _portProjectResult.DownloadedFiles.Add(file); });

        }

        /// <summary>
        /// Initializes the Solution Port
        /// </summary>
        public PortProjectResult AnalysisRun()
        {
            // If the solution was already analyzed, don't duplicate the results
            if (_portProjectResult != null)
            {
                return _portProjectResult;
            }

            _portProjectResult = _projectRewriter.Initialize();

            return _portProjectResult;
        }

        /// <summary>
        /// Runs the Solution Port after creating an analysis
        /// </summary>
        public ProjectResult Run()
        {
            // Find actions to execute for each project
            var projectAnalysisResult = AnalysisRun();
            var projectActions = projectAnalysisResult.ProjectActions;

            // Pass in the actions found to translate all files in each project
            var projectRewriterResult = _projectRewriter.Run(projectActions);
            return projectRewriterResult;
        }
    }
}
