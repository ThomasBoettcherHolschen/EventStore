using System;
using System.Diagnostics;

namespace EventStore.Common.Utils
{
    public class HostName
    {
        public static string Combine(string prefix, Uri requestedUrl, string relativeUri, params object[] arg)
        {
            try
            {
                return CombineHostNameAndPath(requestedUrl, prefix, relativeUri, arg);
            }
            catch (Exception e)
            {
                Debug.WriteLine("Failed to combine hostname with relative path: {0}", e.Message);
                return relativeUri;
            }
        }

        private static string CombineHostNameAndPath(Uri requestedUrl,
                                                     string prefix,
                                                     string relativeUri,
                                                     object[] arg)
        {
            //TODO: encode???
            var path = string.Format(relativeUri, arg);
            path = prefix + path;
            return new UriBuilder(requestedUrl.Scheme, requestedUrl.Host, requestedUrl.Port, path).Uri.AbsoluteUri;
        }
    }
}
