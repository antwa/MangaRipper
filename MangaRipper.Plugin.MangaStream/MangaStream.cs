﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MangaRipper.Core.Interfaces;
using MangaRipper.Core.Models;

namespace MangaRipper.Plugin.MangaStream
{
    /// <summary>
    /// Support find chapters, images from MangaStream
    /// </summary>
    public class MangaStream : IMangaService
    {
        private static ILogger logger;
        private readonly IDownloader downloader;
        private readonly IXPathSelector selector;

        public MangaStream(ILogger myLogger, IDownloader downloader, IXPathSelector selector)
        {
            logger = myLogger;
            this.downloader = downloader;
            this.selector = selector;
        }
        public async Task<IEnumerable<Chapter>> FindChapters(string manga, IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            progress.Report(0);
            // find all chapters in a manga
            string input = await downloader.DownloadStringAsync(manga, cancellationToken);
            var title = selector.Select(input, "//h1").InnerHtml;
            var chaps = selector
                .SelectMany(input, "//td/a")
                .Select(n =>
                {
                    string url = $"https://readms.net{n.Attributes["href"]}";
                    return new Chapter(n.InnerHtml, url) { Manga = title };
                });
            progress.Report(100);
            return chaps;
        }

        public async Task<IEnumerable<string>> FindImages(Chapter chapter, IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            // find all pages in a chapter
            string input = await downloader.DownloadStringAsync(chapter.Url, cancellationToken);
            var pages = selector.SelectMany(input, "//div[contains(@class,'btn-reader-page')]/ul/li/a")
                .Select(n => n.Attributes["href"])
                .Select(p => $"https://readms.net{p}");

            // find all images in pages
            int current = 0;
            var images = new List<string>();
            foreach (var page in pages)
            {
                var pageHtml = await downloader.DownloadStringAsync(page, cancellationToken);
                var image = selector
                .Select(pageHtml, "//img[@id='manga-page']")
                .Attributes["src"];

                images.Add(image);
                var f = (float)++current / pages.Count();
                int i = Convert.ToInt32(f * 100);
                progress.Report(i);
            }
            return images.Select(i => $"https:{i}");
        }

        public SiteInformation GetInformation()
        {
            return new SiteInformation(nameof(MangaStream), "http://readms.net/manga", "English");
        }

        public bool Of(string link)
        {
            var uri = new Uri(link);
            return uri.Host.Equals("readms.net");
        }
    }
}
