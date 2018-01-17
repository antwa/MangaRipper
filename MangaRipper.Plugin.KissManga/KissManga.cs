﻿using MangaRipper.Core.CustomException;
using MangaRipper.Core.Interfaces;
using MangaRipper.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MangaRipper.Plugin.KissManga
{
    /// <summary>
    /// Support find chapters and images from KissManga
    /// </summary>
    public class KissManga : IMangaService
    {
        private static ILogger _logger;
        private readonly IDownloader downloader;
        private IScriptEngine _engine;
        private readonly IXPathSelector selector;

        public KissManga(ILogger myLogger, IDownloader downloader, IXPathSelector selector, IScriptEngine engine)
        {
            _logger = myLogger;
            this.downloader = downloader;
            this._engine = engine;
            this.selector = selector;
        }

        public async Task<IEnumerable<Chapter>> FindChapters(string manga, IProgress<int> progress, CancellationToken cancellationToken)
        {
            progress.Report(0);
            // find all chapters in a manga
            string input = await downloader.DownloadStringAsync(manga, cancellationToken);
            var title = selector.Select(input, "//a[@class='bigChar']").InnerHtml;
            var chaps = selector.SelectMany(input, "//tr/td/a[@title]")
                .Select(n =>
                {
                    string name = n.InnerHtml.Trim();
                    string url = n.Attributes["href"];
                    var chap = NameResolver(name, url, new Uri(manga));
                    chap.Manga = title;
                    return chap;
                });
            progress.Report(100);
            return chaps;
        }

        public async Task<IEnumerable<string>> FindImages(Chapter chapter, IProgress<int> progress, CancellationToken cancellationToken)
        {
            string input = await downloader.DownloadStringAsync(chapter.Url, cancellationToken);

            /// Could be secured against changes by capturing the script's path as it exists in the live document instead of assuming the location.
            string pattern = "<script\\s+(type=[\"']text/javascript[\"'])?\\s+(src=[\"']/Scripts/{0}[\"'])>";
            string concatedPattern = string.Concat(string.Format(pattern, "ca.js"), "|", string.Format(pattern, "lo.js"));

            if (Regex.IsMatch(input, concatedPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            {
                string funcUri = "http://kissmanga.com/Scripts/lo.js";
                string decryptFunc = await downloader.DownloadStringAsync(funcUri, cancellationToken);

                /// Execute CryptoJS (ca.js) from saved resources to reduce HTTP requests.
                _engine.Execute(Properties.Resources.CryptoJs);

                /// Execute the decryption function to allow it to be called later.
                _engine.Execute(decryptFunc);

                var keysPattern = "<script type=\"text/javascript\">[\\s]*(?<Value>.*)(?!</script>)";
                var regex = new Regex(keysPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                var keys = new List<string>();

                foreach (Match match in regex.Matches(input))
                {
                    if (match.Value.Contains("CryptoJS"))
                    {
                        var value = match.Groups["Value"].Value;
                        keys.Add(value);
                        _logger.Debug($"Script to be executed: {value}");
                    }
                }

                if (keys.Count > 0)
                {
                    try
                    {
                        keys.ForEach(key => _engine.Execute(key));
                    }
                    catch (Exception)
                    {
                        _logger.Fatal($"Source: {input}");
                        throw new ArgumentException("Cannot decrypt image URIs.");
                    }
                }
                else
                {
                    _logger.Debug("Keys not found. Continuing.");
                }

                /// As with the script locations, to avoid unnecessary breaking the application, the function name could be captured and invoked 
                /// in the event it changes.
                /// 

                var encryptPages = Parse("lstImages.push\\(wrapKA\\(\"(?<Value>.[^\"]*)\"\\)\\)", input, "Value");

                var pages = encryptPages.Select(e =>
                {
                    string value = string.Empty;

                    try
                    {
                        value = _engine.CallGlobalFunction<string>("wrapKA", e);
                    }
                    catch (Exception ex)
                    {
                        _logger.Fatal($"Source: {input}");
                        _logger.Fatal(ex);
                        throw;
                    }

                    return value;
                });


                pages = pages.Select(p =>
                {
                    var value = new Uri(new Uri(chapter.Url), p).AbsoluteUri;
                    return value;
                }).ToList();

                return pages;
            }

            return null;

        }

        public SiteInformation GetInformation()
        {
            return new SiteInformation("KissManga", "http://kissmanga.com/", "English");
        }

        public bool Of(string link)
        {
            return new Uri(link).Host.Equals("kissmanga.com");
        }

        private Chapter NameResolver(string name, string value, Uri adress)
        {
            var urle = new Uri(adress, value);
            if (!string.IsNullOrWhiteSpace(name))
            {
                name = System.Net.WebUtility.HtmlDecode(name);
                name = Regex.Replace(name, "^Read\\s+|\\s+online$|:", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                name = Regex.Replace(name, "\\s+Read\\s+Online$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            return new Chapter(name, urle.AbsoluteUri);
        }

        private IEnumerable<string> Parse(string regExp, string input, string groupName)
        {
            _logger.Info($"> Parse: {regExp}");
            var reg = new Regex(regExp, RegexOptions.IgnoreCase);
            var matches = reg.Matches(input);

            if (matches.Count == 0)
            {
                _logger.Error("Cannot parse the below content.");
                _logger.Error(input);
                throw new MangaRipperException("Parse content failed! Please check if you can access this content on your browser.");
            }

            var list = (from Match match in matches select match.Groups[groupName].Value.Trim()).ToList();
            var result = list.Distinct().ToList();
            _logger.Info($@"Parse success. There are {result.Count} item(s).");
            return result;
        }
    }
}
