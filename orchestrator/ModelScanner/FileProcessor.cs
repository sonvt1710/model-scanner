﻿using Amazon.Runtime.Internal;
using Amazon.S3.Model.Internal.MarshallTransformations;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Options;
using ModelScanner;
using ModelScanner.Tasks;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

class FileProcessor
{
    readonly ILogger<FileProcessor> _logger;
    readonly IJobTask[] _jobTasks;
    readonly LocalStorageOptions _localStorageOptions;

    public FileProcessor(ILogger<FileProcessor> logger, IEnumerable<IJobTask> jobTasks, IOptions<LocalStorageOptions> options)
    {
        _logger = logger;
        _jobTasks = jobTasks.OrderBy(x => x.TaskType is JobTaskTypes.Hash ? 0 : 1).ToArray(); // Ensure to hash first as this may modify the file
        _localStorageOptions = options.Value;
    }

    [Queue("low-prio"), AutomaticRetry(Attempts = 1)]
    public Task ProcessFileLowPrio(string fileUrl, string callbackUrl, JobTaskTypes tasks, CancellationToken cancellationToken)
        => ProcessFile(fileUrl, callbackUrl, tasks, cancellationToken);


    [Queue("x-low-prio"), AutomaticRetry(Attempts = 1)]
    public Task ProcessFileExtraLowPrio(string fileUrl, string callbackUrl, JobTaskTypes tasks, CancellationToken cancellationToken)
        => ProcessFile(fileUrl, callbackUrl, tasks, cancellationToken);

    [AutomaticRetry(Attempts = 1)]
    public async Task ProcessFile(string fileUrl, string callbackUrl, JobTaskTypes tasks, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_localStorageOptions.TempFolder))
        {
            Directory.CreateDirectory(_localStorageOptions.TempFolder);
        }

        using var httpClient = new HttpClient();

        var result = new ScanResult
        {
            Url = fileUrl
        };

        string? filePath = null;

        try
        {
            _logger.LogInformation("Downloading {fileUrl}", fileUrl);

            using var response = await httpClient.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                result.FileExists = 0;
                await ReportFileAsync(callbackUrl, result, cancellationToken);
                return;
            }

            result.FileExists = 1;
            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Path.GetFileName(new Uri(fileUrl).AbsolutePath);
            filePath = Path.Combine(_localStorageOptions.TempFolder, fileName);

            if (!Path.Exists(filePath) || _localStorageOptions.AlwaysInvalidate)
            {
                using var tempStream = File.Open(filePath, FileMode.Create);
                _logger.LogInformation("Temporary storage: {filePath}", filePath);

                var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await responseStream.CopyToAsync(tempStream, cancellationToken);
            }

            foreach (var jobTask in _jobTasks)
            {
                if (!tasks.HasFlag(jobTask.TaskType))
                {
                    _logger.LogInformation("Skipping over task {jobTask} as it was not requested", jobTask.TaskType);
                    continue;
                }

                _logger.LogInformation("Executing {jobTask}", jobTask.TaskType);
                var continueProcessing = await jobTask.Process(filePath, result, cancellationToken);
                if (!continueProcessing)
                {
                    throw new InvalidOperationException("Conversion aborted");
                }

                await ReportFileAsync(callbackUrl, result, cancellationToken);
            }
        }
        finally
        {
            if (filePath is not null)
            {
                File.Delete(filePath);
            }
        }
    }

    public async Task ReportFileAsync(string callbackUrl, ScanResult result, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();

        _logger.LogInformation("Invoking {callbackUrl} with result {result}", callbackUrl, result);
        var response = await httpClient.PostAsJsonAsync(callbackUrl, result, cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
