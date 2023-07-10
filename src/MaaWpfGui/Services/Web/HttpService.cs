// <copyright file="HttpService.cs" company="MaaAssistantArknights">
// MaaWpfGui - A part of the MaaCoreArknights project
// Copyright (C) 2021 MistEO and Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaaWpfGui.Constants;
using MaaWpfGui.Extensions;
using MaaWpfGui.Helper;
using MaaWpfGui.ViewModels.UI;
using Serilog;

namespace MaaWpfGui.Services.Web
{
    public class HttpService : IHttpService
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/97.0.4692.99 Safari/537.36 Edg/97.0.1072.76";

        private static string Proxy
        {
            get
            {
                var p = ConfigurationHelper.GetValue(ConfigurationKeys.UpdateProxy, string.Empty);
                if (string.IsNullOrEmpty(p))
                {
                    return string.Empty;
                }

                return p.Contains("://") ? p : $"http://{p}";
            }
        }

        private readonly ILogger _logger = Log.ForContext<HttpService>();

        private HttpClient _client;
        private HttpClient _downloader;

        public HttpService()
            {
                ConfigurationHelper.ConfigurationUpdateEvent += (key, old, value) =>
                {
                    if (key != ConfigurationKeys.UpdateProxy)
                    {
                        return;
                    }
            
                    if (old == value)
                    {
                        return;
                    }
            
                    BuildHttpClient(ref _client, TimeSpan.FromSeconds(15));
                    BuildHttpClient(ref _downloader, TimeSpan.FromMinutes(3));
                };
            
                BuildHttpClient(ref _client, TimeSpan.FromSeconds(15));
                BuildHttpClient(ref _downloader, TimeSpan.FromMinutes(3));
            }

        public async Task<double> HeadAsync(Uri uri, Dictionary<string, string> extraHeader = null)
        {
            try
            {
                var request = new HttpRequestMessage { RequestUri = uri, Method = HttpMethod.Head, };

                if (extraHeader != null)
                {
                    foreach (var kvp in extraHeader)
                    {
                        request.Headers.Add(kvp.Key, kvp.Value);
                    }
                }

                var stopwatch = Stopwatch.StartNew();
                var response = await _client.SendAsync(request).ConfigureAwait(false);
                stopwatch.Stop();
                response.Log();

                return response.IsSuccessStatusCode is false ? -1.0 : stopwatch.Elapsed.TotalMilliseconds;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to send GET request to {Uri}", uri);
                return -1.0;
            }
        }

        public async Task<string> GetStringAsync(Uri uri, Dictionary<string, string> extraHeader = null)
        {
            var response = await GetAsync(uri, extraHeader);

            if (response != null)
            {
                return await response.Content.ReadAsStringAsync();
            }

            return null;
        }

        public async Task<Stream> GetStreamAsync(Uri uri, Dictionary<string, string> extraHeader = null)
        {
            var response = await GetAsync(uri, extraHeader);

            if (response != null)
            {
                return await response.Content.ReadAsStreamAsync();
            }

            return null;
        }

        public async Task<HttpResponseMessage> GetAsync(Uri uri, Dictionary<string, string> extraHeader = null, HttpCompletionOption httpCompletionOption = HttpCompletionOption.ResponseContentRead)
        {
            try
            {
                var request = new HttpRequestMessage { RequestUri = uri, Method = HttpMethod.Get, };

                if (extraHeader != null)
                {
                    foreach (var kvp in extraHeader)
                    {
                        request.Headers.Add(kvp.Key, kvp.Value);
                    }
                }

                var response = await _client.SendAsync(request, httpCompletionOption);
                response.Log();

                return response.IsSuccessStatusCode is false ? null : response;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to send GET request to {Uri}", uri);
                return null;
            }
        }

        public async Task<string> PostAsJsonAsync<T>(Uri uri, T content, Dictionary<string, string> extraHeader = null)
        {
            try
            {
                var body = JsonSerializer.Serialize(content);
                var message = new HttpRequestMessage(HttpMethod.Post, uri);
                message.Headers.Accept.ParseAdd("application/json");
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _client.SendAsync(message);
                response.Log();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to send POST request to {Uri}", uri);
                return null;
            }
        }

        public async Task<bool> DownloadFileAsync(Uri uri, string fileName, string contentType = "application/octet-stream")
        {
            string fileDir = Directory.GetCurrentDirectory();
            string fileNameWithTemp = fileName + ".temp";
            string fullFilePath = Path.Combine(fileDir, fileName);
            string fullFilePathWithTemp = Path.Combine(fileDir, fileNameWithTemp);
            _logger.Information("Start to download file from {Uri} and save to {TempPath}", uri, fullFilePathWithTemp);

            var response = await GetAsync(uri, extraHeader: new Dictionary<string, string> { { "Accept", contentType } }, httpCompletionOption: HttpCompletionOption.ResponseHeadersRead);

            if (response is null)
            {
                return false;
            }

            var success = true;
            try
            {
                var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using (var tempFileStream = new FileStream(fullFilePathWithTemp, FileMode.Create, FileAccess.Write))
                {
                    // 记录初始化
                    long value = 0;
                    int valueInOneSecond = 0;
                    long fileMaximum = response.Content.Headers.ContentLength ?? 1;
                    DateTime beforeDt = DateTime.Now;

                    // Dangerous action
                    VersionUpdateViewModel.OutputDownloadProgress();

                    byte[] buffer = new byte[81920];
                    int byteLen = await stream.ReadAsync(buffer, 0, buffer.Length);

                    while (byteLen > 0)
                    {
                        valueInOneSecond += byteLen;
                        double ts = DateTime.Now.Subtract(beforeDt).TotalSeconds;
                        if (ts > 1)
                        {
                            beforeDt = DateTime.Now;
                            value += valueInOneSecond;

                            // Dangerous action
                            VersionUpdateViewModel.OutputDownloadProgress(value, fileMaximum, valueInOneSecond, ts);
                            valueInOneSecond = 0;
                        }

                        // 输入输出
                        tempFileStream.Write(buffer, 0, byteLen);
                        byteLen = await stream.ReadAsync(buffer, 0, buffer.Length);
                    }
                }

                File.Copy(fullFilePathWithTemp, fullFilePath, true);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to copy file stream {TempFile}", fullFilePathWithTemp);
                success = false;
            }
            finally
            {
                if (File.Exists(fullFilePathWithTemp))
                {
                    _logger.Information("Remove download temp file {TempFile}", fullFilePathWithTemp);
                    File.Delete(fullFilePathWithTemp);
                }
            }

            return success;
        }

        private void BuildHttpClient(ref HttpClient client, TimeSpan timeout)
            {
                var proxyIsUri = Uri.TryCreate(Proxy, UriKind.RelativeOrAbsolute, out var uri);
                proxyIsUri = proxyIsUri && (!string.IsNullOrEmpty(Proxy));
                if (proxyIsUri is false)
                {
                    if (!(client is null))
                    {
                        _logger.Information("Proxy is not a valid URI, and HttpClient is not null, keep using the original HttpClient");
                        return;
                    }
                }
            
                _logger.Information("Rebuild HttpClient with proxy {Proxy}", Proxy);
                var handler = new HttpClientHandler { AllowAutoRedirect = true, };
            
                if (proxyIsUri)
                {
                    handler.Proxy = new WebProxy(uri);
                    handler.UseProxy = true;
                }
            
                client?.Dispose();
                client = new HttpClient(handler);
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                client.Timeout = timeout;
            }

            _logger.Information("Rebuild Downloader HttpClient with proxy {Proxy}", Proxy);
            var handler = new HttpClientHandler { AllowAutoRedirect = true, };

            if (proxyIsUri)
            {
                handler.Proxy = new WebProxy(uri);
                handler.UseProxy = true;
            }

            _downloader?.Dispose();
            _downloader = new HttpClient(handler);
            _downloader.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _downloader.Timeout = TimeSpan.FromMinutes(3);
        }
    }
}
