using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Html.Dom;
using Jackett.Common.Models;
using Jackett.Common.Models.IndexerConfig.Bespoke;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Jackett.Common.Utils.Clients;
using Newtonsoft.Json.Linq;
using NLog;
using System.Text.RegularExpressions;

namespace Jackett.Common.Indexers
{
    [ExcludeFromCodeCoverage]
    public class AniLibria : BaseWebIndexer
    {
        public AniLibria(IIndexerConfigurationService configService, Utils.Clients.WebClient wc, Logger l, IProtectionService ps)
            : base(id: "AniLibria",
                   name: "AniLibria",
                   description: "AniLibria is a Public torrent tracker for anime, voiced on russian by AniLibria team",
                   link: "https://www.anilibria.tv/",
                   caps: new TorznabCapabilities(),
                   configService: configService,
                   client: wc,
                   logger: l,
                   p: ps,
                   configData: new ConfigurationDataAniLibria())
        {
            Encoding = Encoding.UTF8;
            Language = "ru-ru";
            Type = "public";

            // Configure the category mappings
            AddCategoryMapping(1, TorznabCatType.TVAnime, "Anime");
        }
        private ConfigurationDataAniLibria Configuration
        {
            get => (ConfigurationDataAniLibria)configData;
            set => configData = value;
        }

        public override async Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson)
        {
            LoadValuesFromJson(configJson);
            var releases = await PerformQuery(new TorznabQuery());

            await ConfigureIfOK(string.Empty, releases.Any(), () =>
                throw new Exception("Could not find releases from this URL"));

            return IndexerConfigurationStatus.Completed;
        }

        // If the search string is empty use the latest releases
        protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
            => query.IsTest || string.IsNullOrWhiteSpace(query.SearchTerm)
            ? await FetchNewReleases()
            : await PerformSearch(query);
        
        private async Task<IEnumerable<ReleaseInfo>> PerformSearch(TorznabQuery query)
        {
            var queryParameters = new NameValueCollection
            {
                { "search", query.SearchTerm },
                { "filter", "names,poster.url,code,torrents.list,season.year" },
            };
            var response = await RequestWithCookiesAndRetryAsync(Configuration.ApiLink.Value + "/searchTitles?" + queryParameters.GetQueryString());
            if (response.Status != System.Net.HttpStatusCode.OK)
                throw new WebException($"AniLibria search returned unexpected result. Expected 200 OK but got {response.Status}.", WebExceptionStatus.ProtocolError);
            
            var results = ParseApiResults(response.ContentString);
            return results.Where(release => query.MatchQueryStringAND(release.Title));
        }

        private async Task<IEnumerable<ReleaseInfo>> FetchNewReleases()
        {
            var queryParameters = new NameValueCollection
            {
                { "limit", "100" },
                { "filter", "names,poster.url,code,torrents.list,season.year" },
            };
            var response = await RequestWithCookiesAndRetryAsync(Configuration.ApiLink.Value + "/getUpdates?" + queryParameters.GetQueryString());
            if (response.Status != System.Net.HttpStatusCode.OK)
                throw new WebException($"AniLibria search returned unexpected result. Expected 200 OK but got {response.Status}.", WebExceptionStatus.ProtocolError);

            return ParseApiResults(response.ContentString);
        }

        private string composeTitle(dynamic json) {
            var title = json.names.ru;
            title += " / " + json.names.en;
            if (json.alternative is string)
                title += " / " + json.names.alternative;
            title += " " + json.season.year;
            return title;
        }

        private List<ReleaseInfo> ParseApiResults(string json)
        {
            var releases = new List<ReleaseInfo>();
            foreach (dynamic r in JArray.Parse(json)) {
                var baseRelease = new ReleaseInfo();
                baseRelease.Title = composeTitle(r);
                baseRelease.BannerUrl = new Uri(Configuration.StaticLink.Value + r.poster.url);
                baseRelease.Comments = new Uri(SiteLink + "/release/" + r.code + ".html");
                baseRelease.DownloadVolumeFactor = 0;
                baseRelease.UploadVolumeFactor = 1;
                baseRelease.Category = new int[]{ TorznabCatType.TVAnime.ID };
                foreach (var t in r.torrents.list) {
                    var release = (ReleaseInfo)baseRelease.Clone();
                    release.Title += " [" + t.quality["string"] + "] - " + t.series["string"];
                    release.Size = t.total_size;
                    release.Seeders = t.seeders;
                    release.Peers = t.leechers + t.seeders;
                    release.Grabs = t.downloads;
                    release.Link = new Uri(SiteLink + t.url);
                    release.PublishDate = new System.DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc).AddSeconds(Convert.ToDouble(t.uploaded_timestamp)).ToLocalTime();
                    releases.Add(release);
                }
            }

            return releases;
        }
    }
}
