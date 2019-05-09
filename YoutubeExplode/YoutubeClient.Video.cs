﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Internal;
using YoutubeExplode.Internal.CipherOperations;
using YoutubeExplode.Internal.Parsers;
using YoutubeExplode.Models;
using YoutubeExplode.Models.ClosedCaptions;
using YoutubeExplode.Models.MediaStreams;

namespace YoutubeExplode
{
    public partial class YoutubeClient
    {
        private readonly Dictionary<string, IReadOnlyList<ICipherOperation>> _cipherOperationsCache =
            new Dictionary<string, IReadOnlyList<ICipherOperation>>();

        private async Task<IReadOnlyDictionary<string, string>> GetVideoInfoAsync(string videoId, string sts = null)
        {
            // This parameter does magic and a lot of videos don't work without it
            var eurl = $"https://youtube.googleapis.com/v/{videoId}".UrlEncode();

            // Execute request
            var url = $"https://www.youtube.com/get_video_info?video_id={videoId}&el=embedded&sts={sts}&eurl={eurl}&hl=en";
            var raw = await _httpClient.GetStringAsync(url);

            // URL-decode the response into a dictionary
            var result = Url.SplitQuery(raw);

            // If video ID is not set - video is unavailable
            if (result.GetValueOrDefault("video_id").IsNullOrWhiteSpace())
                throw new VideoUnavailableException(videoId, $"Video [{videoId}] is unavailable.");

            return result;
        }

        /// <inheritdoc />
        public async Task<Video> GetVideoAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get video info parser
            var videoInfoParser = await GetVideoInfoParserAsync(videoId);

            // Get video watch page parser
            var videoWatchPageParser = await GetVideoWatchPageParserAsync(videoId);

            // Extract info
            var videoAuthor = videoInfoParser.GetVideoAuthor();
            var videoTitle = videoInfoParser.GetVideoTitle();
            var videoDuration = videoInfoParser.GetVideoDuration();
            var videoKeywords = videoInfoParser.GetVideoKeywords();
            var videoUploadDate = videoWatchPageParser.GetVideoUploadDate();
            var videoDescription = videoWatchPageParser.GetVideoDescription();
            var videoViewCount = videoWatchPageParser.GetVideoViewCount();
            var videoLikeCount = videoWatchPageParser.GetVideoLikeCount();
            var videoDislikeCount = videoWatchPageParser.GetVideoDislikeCount();

            var statistics = new Statistics(videoViewCount, videoLikeCount, videoDislikeCount);
            var thumbnails = new ThumbnailSet(videoId);

            return new Video(videoId, videoAuthor, videoUploadDate, videoTitle, videoDescription,
                thumbnails, videoDuration, videoKeywords, statistics);
        }

        /// <inheritdoc />
        public async Task<Channel> GetVideoAuthorChannelAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get video info
            var videoInfo = await GetVideoInfoAsync(videoId);

            // Get player response
            var playerResponseJson = JToken.Parse(videoInfo["player_response"]);

            // Get channel ID
            var channelId = playerResponseJson.SelectToken("videoDetails.channelId").Value<string>();

            return await GetChannelAsync(channelId);
        }

        private async Task<string> DecipherSignatureAsync(string playerSourceUrl, string signature)
        {
            // Try to resolve cipher operations from cache
            var cipherOperations = _cipherOperationsCache.GetValueOrDefault(playerSourceUrl);

            // If they are not in cache - retrieve them
            if (cipherOperations == null)
            {
                // Get player source parser
                var playerSourceParser = await GetPlayerSourceParserAsync(playerSourceUrl);

                // Extract cipher operations and save to cache
                cipherOperations = playerSourceParser.GetCipherOperations();
                _cipherOperationsCache[playerSourceUrl] = cipherOperations;
            }

            // Execute cipher operations on signature
            foreach (var cipherOperation in cipherOperations)
                signature = cipherOperation.Decipher(signature);

            return signature;
        }

        /// <inheritdoc />
        public async Task<MediaStreamInfoSet> GetVideoMediaStreamInfosAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Placeholders for info we're looking to extract
            string dashManifestUrl;
            string hlsManifestUrl;
            DateTimeOffset validUntil;

            // Placeholders for stream info parsers
            IReadOnlyList<UrlEncodedStreamInfoParser> muxedStreamInfoParsers;
            IReadOnlyList<UrlEncodedStreamInfoParser> adaptiveStreamInfoParsers;

            // Get video embed page parser
            var videoEmbedPageParser = await GetVideoEmbedPageParserAsync(videoId);

            // Extract sts and player source URL
            var sts = videoEmbedPageParser.GetSts();
            var playerSourceUrl = videoEmbedPageParser.GetPlayerSourceUrl();

            // Get video info parser
            var requestedAt = DateTimeOffset.Now;
            var videoInfoParser = await GetVideoInfoParserAsync(videoId, sts);

            // Try to get error reason
            var errorReason = videoInfoParser.TryGetErrorReason();

            // If there is no error - proceed
            if (errorReason.IsNullOrWhiteSpace())
            {
                // Extract info
                muxedStreamInfoParsers = videoInfoParser.GetMuxedStreamInfos();
                adaptiveStreamInfoParsers = videoInfoParser.GetAdaptiveStreamInfos();
                dashManifestUrl = videoInfoParser.TryGetDashManifestUrl();
                hlsManifestUrl = videoInfoParser.TryGetHlsManifestUrl();
                validUntil = requestedAt + videoInfoParser.GetExpiresIn();
            }
            // If there's an error - try a different method
            else
            {
                // Get video watch page parser
                requestedAt = DateTimeOffset.Now;
                var videoWatchPageParser = await GetVideoWatchPageParserAsync(videoId);

                // If video requires purchase - throw
                var previewVideoId = videoWatchPageParser.TryGetPreviewVideoId();
                if (!previewVideoId.IsNullOrWhiteSpace())
                {
                    throw new VideoRequiresPurchaseException(videoId, previewVideoId,
                        $"Video [{videoId}] is unplayable because it requires purchase.");
                }

                // If video is unplayable for other reasons - throw
                if (!videoWatchPageParser.TryGetErrorReason().IsNullOrWhiteSpace())
                {
                    throw new VideoUnplayableException(videoId,
                        $"Video [{videoId}] is unplayable. Reason: {errorReason}");
                }

                // Extract info
                muxedStreamInfoParsers = videoWatchPageParser.GetMuxedStreamInfos();
                adaptiveStreamInfoParsers = videoWatchPageParser.GetAdaptiveStreamInfos();
                dashManifestUrl = videoWatchPageParser.TryGetDashManifestUrl();
                hlsManifestUrl = videoWatchPageParser.TryGetHlsManifestUrl();
                validUntil = requestedAt + videoWatchPageParser.GetExpiresIn();
            }

            // Prepare stream info maps
            var muxedStreamInfoMap = new Dictionary<int, MuxedStreamInfo>();
            var audioStreamInfoMap = new Dictionary<int, AudioStreamInfo>();
            var videoStreamInfoMap = new Dictionary<int, VideoStreamInfo>();

            // Extract muxed stream infos
            foreach (var streamInfoParser in muxedStreamInfoParsers)
            {
                // Extract info
                var itag = streamInfoParser.GetItag();
                var url = streamInfoParser.GetUrl();

                // Decipher signature if needed
                var signature = streamInfoParser.TryGetSignature();
                if (!signature.IsNullOrWhiteSpace())
                {
                    signature = await DecipherSignatureAsync(playerSourceUrl, signature);
                    var signatureParameterName = streamInfoParser.TryGetSignatureParameterName() ?? "signature";
                    url = Url.SetQueryParameter(url, signatureParameterName, signature);
                }

                // Try to extract content length, otherwise get it manually
                var contentLength = streamInfoParser.TryGetContentLength() ?? -1;
                if (contentLength <= 0)
                {
                    // Send HEAD request and get content length
                    contentLength = await _httpClient.GetContentLengthAsync(url, false) ?? -1;

                    // If content length is still not available - stream is gone or faulty
                    if (contentLength <= 0)
                        continue;
                }

                // Extract container
                var containerStr = streamInfoParser.GetContainer();
                var container = Heuristics.ContainerFromString(containerStr);

                // Extract audio encoding
                var audioEncodingStr = streamInfoParser.GetAudioEncoding();
                var audioEncoding = Heuristics.AudioEncodingFromString(audioEncodingStr);

                // Extract video encoding
                var videoEncodingStr = streamInfoParser.GetVideoEncoding();
                var videoEncoding = Heuristics.VideoEncodingFromString(videoEncodingStr);

                // Determine video quality from itag
                var videoQuality = Heuristics.VideoQualityFromItag(itag);

                // Determine video quality label from video quality
                var videoQualityLabel = Heuristics.VideoQualityToLabel(videoQuality);

                // Determine video resolution from video quality
                var resolution = Heuristics.VideoQualityToResolution(videoQuality);

                // Add to list
                muxedStreamInfoMap[itag] = new MuxedStreamInfo(itag, url, container, contentLength, audioEncoding, videoEncoding,
                    videoQualityLabel, videoQuality, resolution);
            }

            // Extract adaptive stream infos
            foreach (var streamInfoParser in adaptiveStreamInfoParsers)
            {
                // Extract info
                var itag = streamInfoParser.GetItag();
                var url = streamInfoParser.GetUrl();
                var bitrate = streamInfoParser.GetBitrate();

                // Decipher signature if needed
                var signature = streamInfoParser.TryGetSignature();
                if (!signature.IsNullOrWhiteSpace())
                {
                    signature = await DecipherSignatureAsync(playerSourceUrl, signature);
                    var signatureParameterName = streamInfoParser.TryGetSignatureParameterName() ?? "signature";
                    url = Url.SetQueryParameter(url, signatureParameterName, signature);
                }

                // Try to extract content length, otherwise get it manually
                var contentLength = streamInfoParser.TryGetContentLength() ?? -1;
                if (contentLength <= 0)
                {
                    // Send HEAD request and get content length
                    contentLength = await _httpClient.GetContentLengthAsync(url, false) ?? -1;

                    // If content length is still not available - stream is gone or faulty
                    if (contentLength <= 0)
                        continue;
                }

                // Extract container
                var containerStr = streamInfoParser.GetContainer();
                var container = Heuristics.ContainerFromString(containerStr);

                // If audio-only
                if (streamInfoParser.GetIsAudioOnly())
                {
                    // Extract audio encoding
                    var audioEncodingStr = streamInfoParser.GetAudioEncoding();
                    var audioEncoding = Heuristics.AudioEncodingFromString(audioEncodingStr);

                    // Add stream
                    audioStreamInfoMap[itag] = new AudioStreamInfo(itag, url, container, contentLength, bitrate, audioEncoding);
                }
                // If video-only
                else
                {
                    // Extract video encoding
                    var videoEncodingStr = streamInfoParser.GetVideoEncoding();
                    var videoEncoding = Heuristics.VideoEncodingFromString(videoEncodingStr);

                    // Extract video quality label and video quality
                    var videoQualityLabel = streamInfoParser.GetVideoQualityLabel();
                    var videoQuality = Heuristics.VideoQualityFromLabel(videoQualityLabel);

                    // Extract resolution
                    var width = streamInfoParser.GetWidth();
                    var height = streamInfoParser.GetHeight();
                    var resolution = new VideoResolution(width, height);

                    // Extract framerate
                    var framerate = streamInfoParser.GetFramerate();

                    // Add to list
                    videoStreamInfoMap[itag] = new VideoStreamInfo(itag, url, container, contentLength, bitrate, videoEncoding,
                        videoQualityLabel, videoQuality, resolution, framerate);
                }
            }

            // Extract dash manifest
            if (!dashManifestUrl.IsNullOrWhiteSpace())
            {
                // Extract signature
                var signature = Regex.Match(dashManifestUrl, "/s/(.*?)(?:/|$)").Groups[1].Value;

                // Decipher signature if needed
                if (!signature.IsNullOrWhiteSpace())
                {
                    signature = await DecipherSignatureAsync(playerSourceUrl, signature);
                    dashManifestUrl = Url.SetRouteParameter(dashManifestUrl, "signature", signature);
                }

                // Get the dash manifest parser
                var dashManifestParser = await GetDashManifestParserAsync(dashManifestUrl);

                // Extract dash stream infos
                foreach (var streamInfoParser in dashManifestParser.GetStreamInfos())
                {
                    // Extract info
                    var itag = streamInfoParser.GetItag();
                    var url = streamInfoParser.GetUrl();
                    var contentLength = streamInfoParser.GetContentLength();
                    var bitrate = streamInfoParser.GetBitrate();

                    // Extract container
                    var containerStr = streamInfoParser.GetContainer();
                    var container = Heuristics.ContainerFromString(containerStr);

                    // If audio-only
                    if (streamInfoParser.GetIsAudioOnly())
                    {
                        // Extract audio encoding
                        var audioEncodingStr = streamInfoParser.GetEncoding();
                        var audioEncoding = Heuristics.AudioEncodingFromString(audioEncodingStr);

                        // Add to list
                        audioStreamInfoMap[itag] = new AudioStreamInfo(itag, url, container, contentLength, bitrate, audioEncoding);
                    }
                    // If video-only
                    else
                    {
                        // Extract video encoding
                        var videoEncodingStr = streamInfoParser.GetEncoding();
                        var videoEncoding = Heuristics.VideoEncodingFromString(videoEncodingStr);

                        // Extract resolution
                        var width = streamInfoParser.GetWidth();
                        var height = streamInfoParser.GetHeight();
                        var resolution = new VideoResolution(width, height);

                        // Extract framerate
                        var framerate = streamInfoParser.GetFramerate();

                        // Determine video quality from itag
                        var videoQuality = Heuristics.VideoQualityFromItag(itag);

                        // Determine video quality label from video quality and framerate
                        var videoQualityLabel = Heuristics.VideoQualityToLabel(videoQuality, framerate);

                        // Add to list
                        videoStreamInfoMap[itag] = new VideoStreamInfo(itag, url, container, contentLength, bitrate, videoEncoding,
                            videoQualityLabel, videoQuality, resolution, framerate);
                    }
                }
            }

            // Finalize stream info collections
            var muxedStreamInfos = muxedStreamInfoMap.Values.OrderByDescending(s => s.VideoQuality).ToArray();
            var audioStreamInfos = audioStreamInfoMap.Values.OrderByDescending(s => s.Bitrate).ToArray();
            var videoStreamInfos = videoStreamInfoMap.Values.OrderByDescending(s => s.VideoQuality).ToArray();

            return new MediaStreamInfoSet(muxedStreamInfos, audioStreamInfos, videoStreamInfos, hlsManifestUrl, validUntil);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ClosedCaptionTrackInfo>> GetVideoClosedCaptionTrackInfosAsync(string videoId)
        {
            videoId.GuardNotNull(nameof(videoId));

            if (!ValidateVideoId(videoId))
                throw new ArgumentException($"Invalid YouTube video ID [{videoId}].", nameof(videoId));

            // Get video info
            var videoInfo = await GetVideoInfoAsync(videoId);

            // Get player response
            var playerResponseJson = JToken.Parse(videoInfo["player_response"]);

            // Get closed caption track infos
            var result = new List<ClosedCaptionTrackInfo>();
            foreach (var captionTrackJson in playerResponseJson.SelectToken("..captionTracks").EmptyIfNull())
            {
                // Get URL
                var url = captionTrackJson.SelectToken("baseUrl").Value<string>();

                // Set format to the one we know how to deal with
                url = Url.SetQueryParameter(url, "format", "3");

                // Get language
                var languageCode = captionTrackJson.SelectToken("languageCode").Value<string>();
                var languageName = captionTrackJson.SelectToken("name.simpleText").Value<string>();
                var language = new Language(languageCode, languageName);

                // Get whether the track is autogenerated
                var isAutoGenerated = captionTrackJson.SelectToken("vssId").Value<string>()
                    .StartsWith("a.", StringComparison.OrdinalIgnoreCase);

                // Add to list
                result.Add(new ClosedCaptionTrackInfo(url, language, isAutoGenerated));
            }

            return result;
        }
    }
}