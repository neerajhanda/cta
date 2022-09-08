using System.Collections.Concurrent;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Codelyzer.Analysis;
using Codelyzer.Analysis.Model;
using CTA.FeatureDetection.Common.Models;
using CTA.FeatureDetection.ProjectType.Extensions;
using CTA.Rules.Common.Helpers;
using CTA.Rules.Config;
using CTA.Rules.Models;

namespace CTA.Rules.PortCore;

public class PortCoreUtils
{
    public static HashSet<string> GetReferencesForProject(AnalyzerResult analyzerResult)
    {
        var allReferences = new HashSet<string>();
        var projectResult = analyzerResult.ProjectResult;

        projectResult?.SourceFileResults?.SelectMany(s => s.References)?.Select(r => r.Namespace).Distinct().ToList().ForEach(currentReference =>
        {
            if (currentReference != null && !allReferences.Contains(currentReference))
            {
                allReferences.Add(currentReference);
            }
        });

        projectResult?.SourceFileResults?.SelectMany(s => s.Children.OfType<UsingDirective>())?.Select(u => u.Identifier).Distinct().ToList().ForEach(currentReference =>
        {
            if (currentReference != null && !allReferences.Contains(currentReference))
            {
                allReferences.Add(currentReference);
            }
        });

        projectResult?.SourceFileResults?.SelectMany(s => s.Children.OfType<ImportsStatement>())?.Select(u => u.Identifier).Distinct().ToList().ForEach(currentReference =>
        {
            if (currentReference != null && !allReferences.Contains(currentReference))
            {
                allReferences.Add(currentReference);
            }
        });

        return allReferences;
    }

    public static ProjectType GetProjectType(FeatureDetectionResult projectTypeFeatureResult)
    {
        if (projectTypeFeatureResult.IsVBNetMvcProject())
        {
            return ProjectType.VBNetMvc;
        }
        else if (projectTypeFeatureResult.IsVBWebFormsProject())
        {
            return ProjectType.VBWebForms;
        }
        else if (projectTypeFeatureResult.IsVBWebApiProject())
        {
            return ProjectType.VBWebApi;
        }
        else if (projectTypeFeatureResult.IsVBClassLibraryProject())
        {
            return ProjectType.VBClassLibrary;
        }
        else if (projectTypeFeatureResult.IsMvcProject())
        {
            return ProjectType.Mvc;
        }
        else if (projectTypeFeatureResult.IsWebApiProject())
        {
            return ProjectType.WebApi;
        }
        else if (projectTypeFeatureResult.IsAspNetWebFormsProject())
        {
            return ProjectType.WebForms;
        }
        else if (projectTypeFeatureResult.IsWebClassLibrary())
        {
            return ProjectType.WebClassLibrary;
        }
        else if (projectTypeFeatureResult.IsWCFServiceConfigBasedProject())
        {
            if (projectTypeFeatureResult.HasServiceHostReference())
            {
                return ProjectType.WCFConfigBasedService;
            }
            else
            {
                return ProjectType.WCFServiceLibrary;
            }
        }
        else if (projectTypeFeatureResult.IsWCFServiceCodeBasedProject())
        {
            return ProjectType.WCFCodeBasedService;
        }
        else if (projectTypeFeatureResult.IsWCFClientProject())
        {
            return ProjectType.WCFClient;
        }

        return ProjectType.ClassLibrary;
    }

    public static void CopyOverrideRules(string sourceDir)
    {
        // Skip overriding the same directory.
        if (sourceDir == CTA.Rules.Config.Constants.RulesDefaultPath)
        {
            return;
        }
        var files = Directory.EnumerateFiles(sourceDir, "*.json").ToList();
        files.ForEach(file =>
        {
            File.Copy(file, Path.Combine(CTA.Rules.Config.Constants.RulesDefaultPath, Path.GetFileName(file)), true);
        });
    }

    //public static async Task<List<string>> DownloadRecommendationFiles(HashSet<string> allReferences)
    //{
    //    ConcurrentDictionary<string, bool> skipDownloadFiles = new ConcurrentDictionary<string, bool>();
    //    using var httpClient = new HttpClient();
    //    ConcurrentBag<string> matchedFiles = new ConcurrentBag<string>();

    //    //var parallelOptions = new ParallelOptions() { MaxDegreeOfParallelism = Constants.ThreadCount };
    //    //Parallel.ForEach(allReferences, parallelOptions, async recommendationNamespace =>
    //    foreach (var recommendationNamespace in allReferences)
    //    {
    //        if (!string.IsNullOrEmpty(recommendationNamespace))
    //        {
    //            var fileName = string.Concat(recommendationNamespace.ToLower(), ".json");
    //            var fullFileName = Path.Combine(Constants.RulesDefaultPath, fileName);
    //            try
    //            {
    //                if (skipDownloadFiles.ContainsKey(fullFileName))
    //                {
    //                    continue;
    //                }

    //                //Download only if it's not available
    //                if (!File.Exists(fullFileName))
    //                {
    //                    string fileUrl = $"{Constants.S3RecommendationsBucketUrl}/{fileName}";
    //                    using HttpRequestMessage checkIfPresentRequestMessage =
    //                        new HttpRequestMessage(HttpMethod.Head, fileUrl);
    //                    var response = await httpClient.SendAsync(checkIfPresentRequestMessage);
    //                    if (response.IsSuccessStatusCode)
    //                    {
    //                        await using var stream = await httpClient.GetStreamAsync(fileUrl);
    //                        await using var fileStream = File.Create(fullFileName);
    //                        await stream.CopyToAsync(fileStream);
    //                    }
    //                    else
    //                    {
    //                        skipDownloadFiles.TryAdd(fullFileName, false);
    //                    }
    //                }

    //                matchedFiles.Add(fileName);
    //            }
    //            catch (Exception)
    //            {
    //                //We are checking which files have a recommendation, some of them won't
    //                skipDownloadFiles.TryAdd(fullFileName, false);
    //            }
    //        }
    //    }


    //    LogHelper.LogInformation("Found recommendations for the below:{0}{1}", Environment.NewLine, string.Join(Environment.NewLine, matchedFiles.Distinct()));
    //    return matchedFiles?.ToHashSet<string>()?.ToList();


    //}
}
