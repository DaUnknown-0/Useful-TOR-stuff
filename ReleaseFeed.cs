// Useful TOR Stuff - Copyright (C) 2026 DaUnknown-0
// Licensed under GPL-3.0-or-later. See LICENSE for details.

/*
 * ReleaseFeed - the release list from GitHub's Atom feed instead of the REST API.
 *
 * api.github.com allows 60 unauthenticated requests per hour and IP, and every mod's updater asks
 * once per game start (plus the Mod Manager's re-check). A few restarts in an hour used up the
 * quota and the Mod Manager showed "check unavailable". github.com/<owner>/<repo>/releases.atom is
 * not part of that quota, and neither is a release download from github.com/.../releases/download.
 *
 * The feed carries what the updaters need: the tag (the channel follows from the tag format, vX.Y.Z
 * stable, vX.Y.Z.W test), the title and the release notes. It lists the newest 10 releases, which
 * is always enough for "the newest version". It has no asset list, so the download URL is built
 * from the tag and the plugin's file name; a release whose build failed then answers 404 and the
 * update fails cleanly. The updaters fall back to the API when the feed cannot be read.
 *
 * This file is identical in every mod that uses it (only the namespace differs); keep it that way.
 */

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace UsefulTORStuff {

    internal static class ReleaseFeed {
        public sealed class Entry {
            public string Tag;
            public string Title;
            public string Notes;      // plain text, Markdown-like bullets
            public string Updated;    // ISO 8601
        }

        public static string Url(string owner, string repo) =>
            $"https://github.com/{owner}/{repo}/releases.atom";

        public static string DownloadUrl(string owner, string repo, string tag, string asset) =>
            $"https://github.com/{owner}/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";

        private static readonly Regex EntryRx = new Regex(@"<entry>(.*?)</entry>", RegexOptions.Singleline);
        private static readonly Regex TagRx = new Regex(@"<link[^>]*href=""[^""]*/releases/tag/([^""]+)""", RegexOptions.Singleline);
        private static readonly Regex TitleRx = new Regex(@"<title>(.*?)</title>", RegexOptions.Singleline);
        private static readonly Regex UpdatedRx = new Regex(@"<updated>(.*?)</updated>", RegexOptions.Singleline);
        private static readonly Regex ContentRx = new Regex(@"<content[^>]*>(.*?)</content>", RegexOptions.Singleline);

        /// <summary>The entries of a releases.atom document, newest first; empty when it is not one.</summary>
        public static List<Entry> Parse(string xml) {
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(xml) || xml.IndexOf("<feed", StringComparison.Ordinal) < 0) return list;
            foreach (Match m in EntryRx.Matches(xml)) {
                string body = m.Groups[1].Value;
                var tag = TagRx.Match(body);
                if (!tag.Success) continue;
                var e = new Entry {
                    Tag = Uri.UnescapeDataString(WebUtility.HtmlDecode(tag.Groups[1].Value)),
                    Title = WebUtility.HtmlDecode(TitleRx.Match(body).Groups[1].Value).Trim(),
                    Updated = UpdatedRx.Match(body).Groups[1].Value.Trim(),
                };
                // content is HTML escaped once more inside the XML
                var c = ContentRx.Match(body);
                e.Notes = c.Success ? HtmlToText(WebUtility.HtmlDecode(c.Groups[1].Value)) : "";
                list.Add(e);
            }
            return list;
        }

        // the rendered release notes back to plain text: headings and paragraphs as lines, list items as "- "
        private static string HtmlToText(string html) {
            string s = html;
            s = Regex.Replace(s, @"<\s*br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</\s*(p|h[1-6]|li|ul|ol|div|pre)\s*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }
    }
}
