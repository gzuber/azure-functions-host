// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost.Configuration;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.File;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Management
{
    public class InstanceManager : IInstanceManager
    {
        private static readonly object _assignmentLock = new object();
        private static HostAssignmentContext _assignmentContext;

        private readonly ILogger _logger;
        private readonly IMetricsLogger _metricsLogger;
        private readonly IEnvironment _environment;
        private readonly IOptionsFactory<ScriptApplicationHostOptions> _optionsFactory;
        private readonly HttpClient _client;
        private readonly IScriptWebHostEnvironment _webHostEnvironment;

        public InstanceManager(IOptionsFactory<ScriptApplicationHostOptions> optionsFactory, HttpClient client, IScriptWebHostEnvironment webHostEnvironment,
            IEnvironment environment, ILogger<InstanceManager> logger, IMetricsLogger metricsLogger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _webHostEnvironment = webHostEnvironment ?? throw new ArgumentNullException(nameof(webHostEnvironment));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _metricsLogger = metricsLogger;
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _optionsFactory = optionsFactory ?? throw new ArgumentNullException(nameof(optionsFactory));
        }

        public async Task<string> SpecializeMSISidecar(HostAssignmentContext context)
        {
            string endpoint;
            var msiEnabled = context.IsMSIEnabled(out endpoint);

            _logger.LogInformation($"MSI enabled status: {msiEnabled}");

            if (msiEnabled)
            {
                using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationMSIInit))
                {
                    var uri = new Uri(endpoint);
                    var address = $"http://{uri.Host}:{uri.Port}{ScriptConstants.LinuxMSISpecializationStem}";

                    _logger.LogDebug($"Specializing sidecar at {address}");

                    var requestMessage = new HttpRequestMessage(HttpMethod.Post, address)
                    {
                        Content = new StringContent(JsonConvert.SerializeObject(context.MSIContext),
                            Encoding.UTF8, "application/json")
                    };

                    var response = await _client.SendAsync(requestMessage);

                    _logger.LogInformation($"Specialize MSI sidecar returned {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        var message = $"Specialize MSI sidecar call failed. StatusCode={response.StatusCode}";
                        _logger.LogError(message);
                        return message;
                    }
                }
            }

            return null;
        }

        public bool StartAssignment(HostAssignmentContext context)
        {
            if (!_webHostEnvironment.InStandbyMode)
            {
                _logger.LogError("Assign called while host is not in placeholder mode");
                return false;
            }

            if (_assignmentContext == null)
            {
                lock (_assignmentLock)
                {
                    if (_assignmentContext != null)
                    {
                        return _assignmentContext.Equals(context);
                    }
                    _assignmentContext = context;
                }

                _logger.LogInformation("Starting Assignment");

                // set a flag which will cause any incoming http requests to buffer
                // until specialization is complete
                // the host is guaranteed not to receive any requests until AFTER assign
                // has been initiated, so setting this flag here is sufficient to ensure
                // that any subsequent incoming requests while the assign is in progress
                // will be delayed until complete
                _webHostEnvironment.DelayRequests();

                // start the specialization process in the background
                Task.Run(async () => await Assign(context));

                return true;
            }
            else
            {
                // No lock needed here since _assignmentContext is not null when we are here
                return _assignmentContext.Equals(context);
            }
        }

        public Task<string> ValidateContext(HostAssignmentContext assignmentContext)
        {
            _logger.LogInformation($"Validating host assignment context (SiteId: {assignmentContext.SiteId}, SiteName: '{assignmentContext.SiteName}')");
            // In AppService, ZipUrl == 1 means the package is hosted in azure files.
            // Otherwise we expect zipUrl to be a blobUri to a zip or a squashfs image
            if (!string.IsNullOrEmpty(assignmentContext.ZipUrl) && assignmentContext.ZipUrl != "1")
            {
                return ValidateBlobPackageContext(assignmentContext.ZipUrl);
            }
            else if (!string.IsNullOrEmpty(assignmentContext.AzureFilesConnectionString))
            {
                return ValidateAzureFilesContext(assignmentContext.AzureFilesConnectionString, assignmentContext.AzureFilesContentShare);
            }
            else
            {
                var error = $"Missing ZipUrl and AzureFiles. {nameof(ValidateContext)}";
                _logger.LogError(error);
                return Task.FromResult(error);
            }
        }

        private async Task<string> ValidateBlobPackageContext(string blobUri)
        {
            string error = null;
            HttpResponseMessage response = null;
            try
            {
                if (!string.IsNullOrEmpty(blobUri))
                {
                    // make sure the zip uri is valid and accessible
                    await Utility.InvokeWithRetriesAsync(async () =>
                    {
                        try
                        {
                            using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationZipHead))
                            {
                                var request = new HttpRequestMessage(HttpMethod.Head, blobUri);
                                response = await _client.SendAsync(request);
                                response.EnsureSuccessStatusCode();
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"{MetricEventNames.LinuxContainerSpecializationZipHead} failed");
                            throw;
                        }
                    }, maxRetries: 2, retryInterval: TimeSpan.FromSeconds(0.3)); // Keep this less than ~1s total
                }
            }
            catch (Exception e)
            {
                error = $"Invalid zip url specified (StatusCode: {response?.StatusCode})";
                _logger.LogError(e, "ValidateContext failed");
            }

            return error;
        }

        private async Task<string> ValidateAzureFilesContext(string connectionString, string contentShare)
        {
            try
            {
                var storageAccount = CloudStorageAccount.Parse(connectionString);
                var fileClient = storageAccount.CreateCloudFileClient();
                var share = fileClient.GetShareReference(contentShare);
                await share.CreateIfNotExistsAsync();
                return null;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{nameof(ValidateAzureFilesContext)}");
                return e.Message;
            }
        }

        private async Task Assign(HostAssignmentContext assignmentContext)
        {
            try
            {
                // first make all environment and file system changes required for
                // the host to be specialized
                await ApplyContext(assignmentContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Assign failed");
                throw;
            }
            finally
            {
                // all assignment settings/files have been applied so we can flip
                // the switch now on specialization
                // even if there are failures applying context above, we want to
                // leave placeholder mode
                _logger.LogInformation("Triggering specialization");
                _webHostEnvironment.FlagAsSpecializedAndReady();

                _webHostEnvironment.ResumeRequests();
            }
        }

        private async Task ApplyContext(HostAssignmentContext assignmentContext)
        {
            _logger.LogInformation($"Applying {assignmentContext.Environment.Count} app setting(s)");
            assignmentContext.ApplyAppSettings(_environment);

            // We need to get the non-PlaceholderMode script path so we can unzip to the correct location.
            // This asks the factory to skip the PlaceholderMode check when configuring options.
            var options = _optionsFactory.Create(ScriptApplicationHostOptionsSetup.SkipPlaceholder);

            if (!string.IsNullOrEmpty(assignmentContext.ZipUrl) && assignmentContext.ZipUrl != "1")
            {
                await ApplyBlobPackageContext(assignmentContext.ZipUrl, options.ScriptPath);
            }
            else if (!string.IsNullOrEmpty(assignmentContext.AzureFilesConnectionString))
            {
                ApplyAzureFilesContext(assignmentContext.AzureFilesConnectionString, assignmentContext.AzureFilesContentShare, options.ScriptPath);
            }
        }

        private void ApplyAzureFilesContext(string connectionString, string contentShare, string targetPath)
        {
            var sa = CloudStorageAccount.Parse(connectionString);
            var key = Convert.ToBase64String(sa.Credentials.ExportKey());

            var mountCommand = $"mount -t cifs //{sa.FileEndpoint.Host}/{contentShare} {targetPath} -o vers=3.0,username={sa.Credentials.AccountName},password={key},dir_mode=0777,file_mode=0777,serverino";
            RunBashCommand($"(mkdir -p {targetPath} || true) && ({mountCommand})", MetricEventNames.LinuxContainerSpecializationAzureFilesMount);
        }

        private async Task ApplyBlobPackageContext(string blobUrl, string targetPath)
        {
            // download zip and extract
            var blobUri = new Uri(blobUrl);
            var filePath = await DownloadAsync(blobUri);
            UnpackPackage(filePath, targetPath);

            string bundlePath = Path.Combine(targetPath, "worker-bundle");
            if (Directory.Exists(bundlePath))
            {
                _logger.LogInformation($"Python worker bundle detected");
            }
        }

        private async Task<string> DownloadAsync(Uri zipUri)
        {
            if (!Utility.TryCleanUrl(zipUri.AbsoluteUri, out string cleanedUrl))
            {
                throw new Exception("Invalid url for the package");
            }

            var filePath = Path.Combine(Path.GetTempPath(), Path.GetFileName(zipUri.AbsolutePath));
            _logger.LogInformation($"Downloading zip contents from '{cleanedUrl}' to temp file '{filePath}'");

            HttpResponseMessage response = null;

            await Utility.InvokeWithRetriesAsync(async () =>
            {
                try
                {
                    using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationZipDownload))
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, zipUri);
                        response = await _client.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (Exception e)
                {
                    string error = $"Error downloading zip content {cleanedUrl}";
                    _logger.LogError(e, error);
                    throw;
                }

                _logger.LogInformation($"{response.Content.Headers.ContentLength} bytes downloaded");
            }, 2, TimeSpan.FromSeconds(0.5));

            using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationZipWrite))
            {
                using (var content = await response.Content.ReadAsStreamAsync())
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
                {
                    await content.CopyToAsync(stream);
                }

                _logger.LogInformation($"{response.Content.Headers.ContentLength} bytes written");
            }

            return filePath;
        }

        private void UnpackPackage(string filePath, string scriptPath)
        {
            var packageType = GetPackageType(filePath);

            if (packageType == CodePackageType.Squashfs)
            {
                // default to mount for squashfs images
                if (_environment.IsMountDisabled())
                {
                    UnsquashImage(filePath, scriptPath);
                }
                else
                {
                    MountSquashfsImage(filePath, scriptPath);
                }
            }
            else if (packageType == CodePackageType.Zip)
            {
                // default to unzip for zip packages
                if (_environment.IsMountEnabled())
                {
                    MountZipFile(filePath, scriptPath);
                }
                else
                {
                    UnzipPackage(filePath, scriptPath);
                }
            }
        }

        private CodePackageType GetPackageType(string filePath)
        {
            // Try checking the file extension
            if (FileIsAny(".squashfs", ".sfs", ".sqsh", ".img", ".fs"))
            {
                return CodePackageType.Squashfs;
            }
            else if (FileIsAny(".zip"))
            {
                return CodePackageType.Zip;
            }

            // No file extension match found. Try checking magic number using `file` command.
            (var output, _, _) = RunBashCommand($"file -b {filePath}", MetricEventNames.LinuxContainerSpecializationFileCommand);
            if (output.StartsWith("Squashfs", StringComparison.OrdinalIgnoreCase))
            {
                return CodePackageType.Squashfs;
            }
            else if (output.StartsWith("Zip", StringComparison.OrdinalIgnoreCase))
            {
                return CodePackageType.Zip;
            }
            else
            {
                throw new InvalidOperationException($"Can't find CodePackageType to match {filePath}");
            }

            bool FileIsAny(params string[] options)
                => options.Any(o => filePath.EndsWith(o, StringComparison.OrdinalIgnoreCase));
        }

        private void UnzipPackage(string filePath, string scriptPath)
        {
            using (_metricsLogger.LatencyEvent(MetricEventNames.LinuxContainerSpecializationZipExtract))
            {
                _logger.LogInformation($"Extracting files to '{scriptPath}'");
                ZipFile.ExtractToDirectory(filePath, scriptPath, overwriteFiles: true);
                _logger.LogInformation($"Zip extraction complete");
            }
        }

        private void UnsquashImage(string filePath, string scriptPath)
            => RunBashCommand($"unsquashfs -f -d '{scriptPath}' '{filePath}'", MetricEventNames.LinuxContainerSpecializationUnsquash);

        private void MountSquashfsImage(string filePath, string scriptPath)
            => RunFuseMount($"squashfuse_ll '{filePath}' '{scriptPath}'", scriptPath);

        private void MountZipFile(string filePath, string scriptPath)
            => RunFuseMount($"fuse-zip -r '{filePath}' '{scriptPath}'", scriptPath);

        private void RunFuseMount(string mountCommand, string targetPath)
            => RunBashCommand($"(mknod /dev/fuse c 10 229 || true) && (mkdir -p '{targetPath}' || true) && ({mountCommand})", MetricEventNames.LinuxContainerSpecializationFuseMount);

        private (string, string, int) RunBashCommand(string command, string metricName)
        {
            using (_metricsLogger.LatencyEvent(metricName))
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bash",
                        Arguments = $"-c \"{command}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                _logger.LogInformation($"Running: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                process.Start();
                var output = process.StandardOutput.ReadToEnd().Trim();
                var error = process.StandardError.ReadToEnd().Trim();
                process.WaitForExit();
                _logger.LogInformation($"Output: {output}");
                if (process.ExitCode != 0)
                {
                    _logger.LogError(error);
                }
                else
                {
                    _logger.LogInformation($"error: {error}");
                }
                _logger.LogInformation($"exitCode: {process.ExitCode}");
                return (output, error, process.ExitCode);
            }
        }

        public IDictionary<string, string> GetInstanceInfo()
        {
            return new Dictionary<string, string>
            {
                { "FUNCTIONS_EXTENSION_VERSION", ScriptHost.Version },
                { "WEBSITE_NODE_DEFAULT_VERSION", "8.5.0" }
            };
        }

        // for testing
        internal static void Reset()
        {
            _assignmentContext = null;
        }
    }
}
