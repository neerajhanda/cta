using CTA.Rules.Common.Interfaces;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CTA.Rules.Common.Helpers;

public class CachedHttpService : ICachedHttpService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly bool _cachingEnabled;
    private readonly ConcurrentDictionary<(string, string), string> _fileETagMap = new();
    private readonly string _tempDirectory;
    private readonly CachedHttpServiceOptions _options;
    private ConcurrentDictionary<string, int> _notFoundFiles = new();
    public CachedHttpService(IHttpClientFactory httpClientFactory, ILogger<CachedHttpService> logger, CachedHttpServiceOptions options)
    {
        _httpClient = httpClientFactory.CreateClient(options.ServiceName);
        if (options.BaseAddress is not null)
        {
            _httpClient.BaseAddress = options.BaseAddress;
        }

        _logger = logger;
        _options = options;
        try
        {
            _tempDirectory = CreateTempDirectory(options.ServiceName, options.SolutionId);
            _cachingEnabled = true;
        }
        catch (Exception e)
        {
            _cachingEnabled = false;
            logger?.LogError(e, "Could not create temp directory for {0}. Caching disabled", options.SolutionId ?? "EMPTY");
        }
    }
    public async Task<bool> DoesFileExistAsync(string filePath)
    {
        if (_notFoundFiles.ContainsKey(filePath)) return false;
        var exists = await CheckIfFileExists(_httpClient, filePath);
        if (!exists)
        {
            _notFoundFiles[filePath] = 1;
        }

        return exists;
    }

    public async Task<Stream> DownloadFileAsync(string filePath)
    {
        if (!_cachingEnabled)
        {
            return await _httpClient.GetStreamAsync(filePath);
        }
        var headResponse = await GetHeadResponse(_httpClient, filePath);
        var key = (_httpClient.BaseAddress?.AbsoluteUri ?? String.Empty, filePath);
        var fileSystemFilePath = Path.Combine(_tempDirectory, filePath);
        if (_fileETagMap.TryGetValue(key, out var eTag))
        {
            if (headResponse?.Headers?.ETag?.Tag == eTag)
            {
                //Tag matches. Return from filesystem

                if (File.Exists(fileSystemFilePath))
                {
                    return File.OpenRead(fileSystemFilePath);
                }
                //File does not exist. Download.
                return await DownloadFile(filePath, headResponse, fileSystemFilePath);
            }
            else
            {
                //tag does not match. Newer file is available.Download.
                return await DownloadFile(filePath, headResponse, fileSystemFilePath);
            }

        }
        else
        {
            //Tag not found in map. Download.
            return await DownloadFile(filePath, headResponse, fileSystemFilePath);
        }
    }

    private async Task<Stream> DownloadFile(string filePath, HttpResponseMessage headResponse, string fileSystemFilePath)
    {
        UpdateFileETagMap(_httpClient, filePath, headResponse);
        await using (var stream = await _httpClient.GetStreamAsync(filePath))
        {
            string directoryName = Path.GetDirectoryName(fileSystemFilePath);
            Directory.CreateDirectory(directoryName);
            await using (var fileStream = File.Create(fileSystemFilePath))
            {
                await stream.CopyToAsync(fileStream);
                await stream.FlushAsync();
                await fileStream.FlushAsync();
            }
        }
        return File.OpenRead(fileSystemFilePath);
    }

    private async Task<bool> CheckIfFileExists(HttpClient httpClient, string filePath)
    {
        var response = await GetHeadResponse(httpClient, filePath);
        return response.IsSuccessStatusCode;
    }

    private void UpdateFileETagMap(HttpClient httpClient, string filePath, HttpResponseMessage response)
    {
        var eTag = response.Headers.ETag;
        if (eTag is not null)
        {
            if (httpClient.BaseAddress != null)
                _fileETagMap[(httpClient.BaseAddress?.AbsoluteUri ?? String.Empty, filePath)] = eTag!.Tag;
        }
    }

    private async Task<HttpResponseMessage> GetHeadResponse(HttpClient httpClient, string filePath)
    {
        using HttpRequestMessage checkIfPresentRequestMessage =
            new HttpRequestMessage(HttpMethod.Head, filePath);
        var response = await httpClient.SendAsync(checkIfPresentRequestMessage);
        return response;
    }
    private string CreateTempDirectory(string serviceName, string solutionId)
    {
        string solutionHash;
        if (!String.IsNullOrWhiteSpace(solutionId))
        {
            using var sha = SHA256.Create();
            byte[] textData = System.Text.Encoding.UTF8.GetBytes(solutionId);
            byte[] hash = sha.ComputeHash(textData);
            solutionHash = BitConverter.ToString(hash);
        }
        else
        {
            solutionHash = Path.GetRandomFileName();
        }

        var tempSolutionDirectory = Path.Combine(Path.GetTempPath(), solutionHash, serviceName);
        tempSolutionDirectory = tempSolutionDirectory.Replace("-", "");
        Directory.CreateDirectory(tempSolutionDirectory);
        return tempSolutionDirectory;

    }
}
