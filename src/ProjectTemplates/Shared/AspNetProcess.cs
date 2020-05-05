// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using Xunit;
using Xunit.Abstractions;

namespace Templates.Test.Helpers
{
    [DebuggerDisplay("{ToString(),nq}")]
    public class AspNetProcess : IDisposable
    {
        private const string ListeningMessagePrefix = "Now listening on: ";
        private readonly HttpClient _httpClient;
        private readonly ITestOutputHelper _output;

        private string _certificatePath;
        private string _certificatePassword = Guid.NewGuid().ToString();
        private string _certificateThumbprint;

        internal readonly Uri ListeningUri;
        internal ProcessEx Process { get; }

        public AspNetProcess(
            ITestOutputHelper output,
            string workingDirectory,
            string dllPath,
            IDictionary<string, string> environmentVariables,
            bool published = true,
            bool hasListeningUri = true,
            ILogger logger = null)
        {
            _output = output;
            _httpClient = new HttpClient(new HttpClientHandler()
            {
                AllowAutoRedirect = true,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) => certificate?.Thumbprint == _certificateThumbprint,
            })
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            _certificatePath = Path.Combine(workingDirectory, $"{Guid.NewGuid()}.pfx");

            EnsureDevelopmentCertificates();

            output.WriteLine("Running ASP.NET application...");

            var arguments = published ? $"exec {dllPath}" : "run";

            logger?.LogInformation($"AspNetProcess - process: {DotNetMuxer.MuxerPathOrDefault()} arguments: {arguments}");

            var finalEnvironmentVariables = new Dictionary<string, string>(environmentVariables)
            {
                ["ASPNETCORE_Kestrel__Certificates__Default__Path"] = _certificatePath,
                ["ASPNETCORE_Kestrel__Certificates__Default__Password"] = _certificatePassword
            };

            Process = ProcessEx.Run(output, workingDirectory, DotNetMuxer.MuxerPathOrDefault(), arguments, envVars: finalEnvironmentVariables);

            logger?.LogInformation("AspNetProcess - process started");

            if (hasListeningUri)
            {
                logger?.LogInformation("AspNetProcess - Getting listening uri");
                ListeningUri = ResolveListeningUrl(output);
                logger?.LogInformation($"AspNetProcess - Got {ListeningUri}");
            }
        }

        internal void EnsureDevelopmentCertificates()
        {
            var now = DateTimeOffset.Now;
            var manager = CertificateManager.Instance;
            var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(now, now.AddYears(1));
            _certificateThumbprint = certificate.Thumbprint;
            manager.ExportCertificate(certificate, path: _certificatePath, includePrivateKey: true, _certificatePassword);
        }

        public void VisitInBrowser(IWebDriver driver)
        {
            _output.WriteLine($"Opening browser at {ListeningUri}...");
            driver.Navigate().GoToUrl(ListeningUri);

            if (driver is EdgeDriver)
            {
                // Workaround for untrusted ASP.NET Core development certificates.
                // The edge driver doesn't supported skipping the SSL warning page.

                if (driver.Title.Contains("Certificate error", StringComparison.OrdinalIgnoreCase))
                {
                    _output.WriteLine("Page contains certificate error. Attempting to get around this...");
                    driver.FindElement(By.Id("moreInformationDropdownSpan")).Click();
                    var continueLink = driver.FindElement(By.Id("invalidcert_continue"));
                    if (continueLink != null)
                    {
                        _output.WriteLine($"Clicking on link '{continueLink.Text}' to skip invalid certificate error page.");
                        continueLink.Click();
                        driver.Navigate().GoToUrl(ListeningUri);
                    }
                    else
                    {
                        _output.WriteLine("Could not find link to skip certificate error page.");
                    }
                }
            }
        }

        public async Task AssertPagesOk(IEnumerable<Page> pages)
        {
            foreach (var page in pages)
            {
                await AssertOk(page.Url);
                await ContainsLinks(page);
            }
        }

        public async Task ContainsLinks(Page page)
        {
            var response = await RequestWithRetries(client =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(ListeningUri, page.Url));
                return client.SendAsync(request);
            }, _httpClient);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var parser = new HtmlParser();
            var html = await parser.ParseAsync(await response.Content.ReadAsStreamAsync());

            foreach (IHtmlLinkElement styleSheet in html.GetElementsByTagName("link"))
            {
                Assert.Equal("stylesheet", styleSheet.Relation);
                await AssertOk(styleSheet.Href.Replace("about://", string.Empty));
            }
            foreach (var script in html.Scripts)
            {
                if (!string.IsNullOrEmpty(script.Source))
                {
                    await AssertOk(script.Source);
                }
            }

            Assert.True(html.Links.Length == page.Links.Count(), $"Expected {page.Url} to have {page.Links.Count()} links but it had {html.Links.Length}");
            foreach ((var link, var expectedLink) in html.Links.Zip(page.Links, Tuple.Create))
            {
                IHtmlAnchorElement anchor = (IHtmlAnchorElement)link;
                if (string.Equals(anchor.Protocol, "about:"))
                {
                    Assert.True(anchor.PathName.EndsWith(expectedLink), $"Expected next link on {page.Url} to be {expectedLink} but it was {anchor.PathName}.");
                    await AssertOk(anchor.PathName);
                }
                else
                {
                    Assert.True(string.Equals(anchor.Href, expectedLink), $"Expected next link to be {expectedLink} but it was {anchor.Href}.");
                    var result = await RetryHelper.RetryRequest(async () =>
                    {
                        return await RequestWithRetries(client => client.GetAsync(anchor.Href), _httpClient);
                    }, logger: NullLogger.Instance);

                    Assert.True(IsSuccessStatusCode(result), $"{anchor.Href} is a broken link!");
                }
            }
        }

        private async Task<T> RequestWithRetries<T>(Func<HttpClient, Task<T>> requester, HttpClient client, int retries = 3, TimeSpan initialDelay = default)
        {
            var currentDelay = initialDelay == default ? TimeSpan.FromSeconds(30) : initialDelay;
            for (int i = 0; i <= retries; i++)
            {
                try
                {
                    return await requester(client);
                }
                catch (Exception)
                {
                    if (i == retries)
                    {
                        throw;
                    }
                    await Task.Delay(currentDelay);
                    currentDelay *= 2;
                }
            }
            throw new InvalidOperationException("Max retries reached.");
        }

        private Uri ResolveListeningUrl(ITestOutputHelper output)
        {
            // Wait until the app is accepting HTTP requests
            output.WriteLine("Waiting until ASP.NET application is accepting connections...");
            var listeningMessage = GetListeningMessage();

            if (!string.IsNullOrEmpty(listeningMessage))
            {
                listeningMessage = listeningMessage.Trim();
                // Verify we have a valid URL to make requests to
                var listeningUrlString = listeningMessage.Substring(listeningMessage.IndexOf(
                    ListeningMessagePrefix, StringComparison.Ordinal) + ListeningMessagePrefix.Length);

                output.WriteLine($"Detected that ASP.NET application is accepting connections on: {listeningUrlString}");
                listeningUrlString = listeningUrlString.Substring(0, listeningUrlString.IndexOf(':')) +
                    "://localhost" +
                    listeningUrlString.Substring(listeningUrlString.LastIndexOf(':'));

                output.WriteLine("Sending requests to " + listeningUrlString);
                return new Uri(listeningUrlString, UriKind.Absolute);
            }
            else
            {
                return null;
            }
        }

        private string GetListeningMessage()
        {
            var buffer = new List<string>();
            try
            {
                foreach (var line in Process.OutputLinesAsEnumerable)
                {
                    if (line != null)
                    {
                        buffer.Add(line);
                        if (line.Trim().Contains(ListeningMessagePrefix, StringComparison.Ordinal))
                        {
                            return line;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            throw new InvalidOperationException(@$"Couldn't find listening url:
{string.Join(Environment.NewLine, buffer)}");
        }

        private bool IsSuccessStatusCode(HttpResponseMessage response)
        {
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Redirect;
        }

        public Task AssertOk(string requestUrl)
            => AssertStatusCode(requestUrl, HttpStatusCode.OK);

        public Task AssertNotFound(string requestUrl)
            => AssertStatusCode(requestUrl, HttpStatusCode.NotFound);

        internal Task<HttpResponseMessage> SendRequest(string path)
        {
            return RequestWithRetries(client => client.GetAsync(new Uri(ListeningUri, path)), _httpClient);
        }

        public async Task AssertStatusCode(string requestUrl, HttpStatusCode statusCode, string acceptContentType = null)
        {
            var response = await RequestWithRetries(client =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(ListeningUri, requestUrl));

                if (!string.IsNullOrEmpty(acceptContentType))
                {
                    request.Headers.Add("Accept", acceptContentType);
                }

                return client.SendAsync(request);
            }, _httpClient);
            Assert.True(statusCode == response.StatusCode, $"Expected {requestUrl} to have status '{statusCode}' but it was '{response.StatusCode}'.");
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            Process.Dispose();
        }

        public override string ToString()
        {
            var result = "";
            result += Process != null ? "Active: " : "Inactive";
            if (Process != null)
            {
                if (!Process.HasExited)
                {
                    result += $"(Listening on {ListeningUri.OriginalString}) PID: {Process.Id}";
                }
                else
                {
                    result += "(Already finished)";
                }
            }

            return result;
        }
    }

    public class Page
    {
        public string Url { get; set; }
        public IEnumerable<string> Links { get; set; }
    }
}
