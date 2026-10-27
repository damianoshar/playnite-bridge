namespace PlayniteBridge.Server
{
    internal class ImageResult
    {
        public byte[] Data { get; set; }
        public string ContentType { get; set; }
        /// <summary>Strong ETag; enables 304 Not Modified replies.</summary>
        public string ETag { get; set; }
    }
}
