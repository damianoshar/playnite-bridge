namespace PlayniteBridge.Services
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Linq;
    using Playnite.SDK;
    using PlayniteBridge.Server;

    /// <summary>
    /// Serves game artwork without hammering the PC.
    /// - In-memory cache (256 MB cap) keyed by game+type+width, validated
    ///   against the file's last-write tick on every hit (art changes apply).
    /// - All fetches serialized through one lock: duplicate concurrent reads
    ///   collapse and disk reads never pile up.
    /// - Optional server-side thumbnails (?width=) so scrolling clients can
    ///   pull ~50 KB files instead of multi-MB originals.
    /// - Strong ETags so clients (and the 304 path in HttpApiServer) skip
    ///   re-downloads entirely.
    /// </summary>
    internal class CoverService
    {
        private class Entry
        {
            public byte[] Data;
            public string ContentType;
            public string ETag;
            public long Ticks;
            public long Length;
        }

        private readonly IPlayniteAPI _api;
        private readonly object _fetchLock = new object();
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>();
        private long _cachedBytes;
        private const long MaxCachedBytes = 256L * 1024 * 1024;

        public CoverService(IPlayniteAPI api)
        {
            _api = api;
        }

        /// <summary>
        /// Returns the image, or null on miss. statusCode/message mirror the
        /// legacy handler strings so API responses are unchanged.
        /// </summary>
        public ImageResult GetCoverImage(Guid gameId, string type, int? maxWidth, out int statusCode, out string errorMessage)
        {
            statusCode = 404;
            errorMessage = null;

            if (type != "cover" && type != "icon" && type != "background")
            {
                errorMessage = $"No {type} image for this game";
                return null;
            }

            var game = _api.Database.Games.Get(gameId);
            if (game == null)
            {
                errorMessage = "Game not found";
                return null;
            }

            string dbPath = type == "cover" ? game.CoverImage : type == "icon" ? game.Icon : game.BackgroundImage;
            if (string.IsNullOrEmpty(dbPath))
            {
                errorMessage = $"No {type} image for this game";
                return null;
            }

            string fullPath;
            try
            {
                fullPath = _api.Database.GetFullFilePath(dbPath);
            }
            catch (Exception ex)
            {
                statusCode = 500;
                errorMessage = "Failed to read image: " + ex.Message;
                return null;
            }
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                errorMessage = "Image file not found on disk";
                return null;
            }

            var key = gameId + ":" + type + ":w" + (maxWidth?.ToString() ?? "full");

            lock (_fetchLock)
            {
                Entry hit;
                if (_cache.TryGetValue(key, out hit) && FileUnchanged(fullPath, hit))
                    return new ImageResult { Data = hit.Data, ContentType = hit.ContentType, ETag = hit.ETag };

                try
                {
                    var fileInfo = new FileInfo(fullPath);
                    var bytes = File.ReadAllBytes(fullPath);
                    var contentType = ContentTypeFor(fullPath);
                    var ext = Path.GetExtension(fullPath).ToLower();

                    if (maxWidth.HasValue)
                    {
                        var resized = TryResize(bytes, ext, maxWidth.Value, out var resizedType);
                        if (resized != null)
                        {
                            bytes = resized;
                            contentType = resizedType;
                        }
                    }

                    var etag = "\"" + bytes.Length.ToString("x") + "-" + fileInfo.LastWriteTimeUtc.Ticks.ToString("x") + "\"";
                    Store(key, new Entry
                    {
                        Data = bytes,
                        ContentType = contentType,
                        ETag = etag,
                        Ticks = fileInfo.LastWriteTimeUtc.Ticks,
                        Length = fileInfo.Length
                    });
                    return new ImageResult { Data = bytes, ContentType = contentType, ETag = etag };
                }
                catch (Exception ex)
                {
                    statusCode = 500;
                    errorMessage = "Failed to read image: " + ex.Message;
                    return null;
                }
            }
        }

        private static bool FileUnchanged(string fullPath, Entry entry)
        {
            try
            {
                var info = new FileInfo(fullPath);
                return info.Exists && info.LastWriteTimeUtc.Ticks == entry.Ticks && info.Length == entry.Length;
            }
            catch
            {
                return false;
            }
        }

        private void Store(string key, Entry entry)
        {
            Entry old;
            if (_cache.TryGetValue(key, out old))
                _cachedBytes -= old.Data?.Length ?? 0;

            if (_cachedBytes + entry.Data.Length > MaxCachedBytes)
            {
                _cache.Clear();
                _cachedBytes = 0;
            }

            _cache[key] = entry;
            _cachedBytes += entry.Data.Length;
        }

        private static string ContentTypeFor(string fullPath)
        {
            var ext = Path.GetExtension(fullPath).ToLower();
            if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
            if (ext == ".gif") return "image/gif";
            if (ext == ".webp") return "image/webp";
            if (ext == ".ico") return "image/x-icon";
            if (ext == ".tga") return "image/x-tga";
            return "image/png";
        }

        private static byte[] TryResize(byte[] source, string ext, int maxWidth, out string contentType)
        {
            contentType = null;
            // Never touch animated/vector-ish formats; System.Drawing may also
            // lack a webp codec, so any failure falls back to the original.
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                return null;
            try
            {
                using (var ms = new MemoryStream(source))
                using (var img = Image.FromStream(ms))
                {
                    if (img.Width <= maxWidth || img.Width <= 0)
                        return null;
                    var height = Math.Max(1, (int)(img.Height * (maxWidth / (double)img.Width)));
                    using (var thumb = new Bitmap(maxWidth, height))
                    {
                        using (var g = Graphics.FromImage(thumb))
                        {
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.DrawImage(img, 0, 0, maxWidth, height);
                        }
                        using (var outMs = new MemoryStream())
                        {
                            if (ext == ".png")
                            {
                                thumb.Save(outMs, ImageFormat.Png);
                                contentType = "image/png";
                            }
                            else
                            {
                                var codec = ImageCodecInfo.GetImageEncoders()
                                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                if (codec == null) return null;
                                using (var encoderParams = new EncoderParameters(1))
                                {
                                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
                                    thumb.Save(outMs, codec, encoderParams);
                                }
                                contentType = "image/jpeg";
                            }
                            return outMs.ToArray();
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
