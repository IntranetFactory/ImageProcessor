using ImageProcessor;
using ImageProcessor.Imaging.Formats;
using ImageProcessor.Web;
using ImageProcessor.Web.Caching;
using ImageProcessor.Web.Configuration;
using ImageProcessor.Web.Helpers;
using ImageProcessor.Web.Services;
using ImageProcessor.Web.Extensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;
using System.Web.Mvc;

namespace Test_Website_NET45.Controllers
{
    
    public class RemoteImageController : Controller
    {
        public RemoteImageController()
        {
            if (preserveExifMetaData == null)
            {
                preserveExifMetaData = ImageProcessorConfiguration.Instance.PreserveExifMetaData;
            }

            if (fixGamma == null)
            {
                fixGamma = ImageProcessorConfiguration.Instance.FixGamma;
            }
        }

        public async Task<ActionResult> Index(string url)
        {
            string fullPath = await ProcessImageAsync(this.HttpContext.ApplicationInstance.Context);

            if (fullPath != null)
            {
                System.IO.FileInfo fileInfo = new FileInfo(fullPath);
                string extension = fileInfo.Extension;
                string contentType = string.Format("image/{0}", extension);

                FileStream file = System.IO.File.Open(fullPath, FileMode.Open);

                return File(file, contentType);
            }
            else
            {
                return new HttpNotFoundResult();
            }
        }

        #region Private
        /// <summary>
        /// The key for storing the response type of the current image.
        /// </summary>
        private const string CachedResponseTypeKey = "CACHED_IMAGE_RESPONSE_TYPE_054F217C-11CF-49FF-8D2F-698E8E6EB58F";

        /// <summary>
        /// The key for storing the cached path of the current image.
        /// </summary>
        private const string CachedPathKey = "CACHED_IMAGE_PATH_TYPE_E0741478-C17B-433D-96A8-6CDA797644E9";

        /// <summary>
        /// The key for storing the file dependency of the current image.
        /// </summary>
        private const string CachedResponseFileDependency = "CACHED_IMAGE_DEPENDENCY_054F217C-11CF-49FF-8D2F-698E8E6EB58F";

        /// <summary>
        /// The regular expression to search strings for protocols with.
        /// </summary>
        private static readonly Regex ProtocolRegex = new Regex("http(s)?://", RegexOptions.Compiled);

        /// <summary>
        /// The regular expression to search strings for presets with.
        /// </summary>
        private static readonly Regex PresetRegex = new Regex(@"preset=[^&]+", RegexOptions.Compiled);

        /// <summary>
        /// The event that is called when a querystring is processed.
        /// </summary>
        public static event ImageProcessor.Web.HttpModules.ImageProcessingModule.ProcessQuerystringEventHandler OnProcessQuerystring;

        /// <summary>
        /// Ensures duplicate requests are atomic.
        /// </summary>
        private static readonly AsyncDuplicateLock Locker = new AsyncDuplicateLock();

        /// <summary>
        /// The image cache.
        /// </summary>
        private IImageCache imageCache;

        /// <summary>
        /// Whether to preserve exif meta data.
        /// </summary>
        private static bool? preserveExifMetaData;

        /// <summary>
        /// Whether to perform gamma correction when performing processing.
        /// </summary>
        private static bool? fixGamma;

        /// <summary>
        /// Processes the image.
        /// </summary>
        /// <param name="context">
        /// the <see cref="T:System.Web.HttpContext">HttpContext</see> object that provides
        /// references to the intrinsic server objects
        /// </param>
        /// <returns>
        /// The <see cref="T:System.Threading.Tasks.Task"/>.
        /// </returns>
        private async Task<string> ProcessImageAsync(HttpContext context)
        {
            HttpRequest request = context.Request;

            // Should we ignore this request?
            if (request.Unvalidated.RawUrl.ToUpperInvariant().Contains("IPIGNORE=TRUE"))
            {
                return null;
            }

            IImageService currentService = new RemoteImageService(); //this.GetImageServiceForRequest(request);
            currentService.Prefix = "rimage";
            try { 
            currentService.WhiteList = new Uri[] { 
                new Uri("http://ipcache.blob.core.windows.net/"),
                new Uri("http://stormmanagementstaging.blob.core.windows.net/"),
                new Uri("http://images.mymovies.net"),
                new Uri("http://core.windows.net"),
                new Uri("http://maps.googleapis.com"),
                new Uri("http://fbcdn"),
                new Uri("http://fbcdn-profile-"),
                new Uri("https://profile."),
                new Uri("http://profile."),
                new Uri("https://pbs.twimg.com"),
                new Uri("http://pbs.twimg.com"),
                new Uri("http://placekitten.com"),
                new Uri("http://hanselminutes.com/")
            };
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }

            if (currentService != null)
            {
                bool isFileLocal = currentService.IsFileLocalService;
                string url = request.Url.ToString();
                bool isLegacy = ProtocolRegex.Matches(url).Count > 1;
                bool hasMultiParams = url.Count(f => f == '?') > 1;
                string requestPath;
                string queryString = string.Empty;
                string urlParameters = string.Empty;

                // Legacy support. I'd like to remove this asap.
                if (isLegacy && hasMultiParams)
                {
                    // We need to split the querystring to get the actual values we want.
                    string[] paths = url.Split('?');
                    requestPath = paths[1];

                    // Handle extension-less urls.
                    if (paths.Length > 3)
                    {
                        queryString = paths[3];
                        urlParameters = paths[2];
                    }
                    else if (paths.Length > 1)
                    {
                        queryString = paths[2];
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(currentService.Prefix))
                    {
                        requestPath = currentService.IsFileLocalService
                            ? HostingEnvironment.MapPath(request.Path)
                            : request.Path;
                        queryString = request.QueryString.ToString();
                    }
                    else
                    {
                        // Parse any protocol values from settings.
                        string protocol = currentService.Settings.ContainsKey("Protocol")
                                              ? currentService.Settings["Protocol"] + "://"
                                              : currentService.GetType() == typeof(RemoteImageService) ? request.Url.Scheme + "://" : string.Empty;

                        // Handle requests that require parameters.
                        if (hasMultiParams)
                        {
                            string[] paths = url.Split('?');
                            requestPath = protocol
                                          + request.Path.TrimStart('/').Remove(0, currentService.Prefix.Length).TrimStart('/')
                                          + "?" + paths[1];
                            queryString = paths[2];
                        }
                        else
                        {
                            requestPath = protocol + request.Path.TrimStart('/').Remove(0, currentService.Prefix.Length).TrimStart('/');
                            queryString = request.QueryString.ToString();
                        }
                    }
                }

                // Replace any presets in the querystring with the actual value.
                queryString = this.ReplacePresetsInQueryString(queryString);

                // Execute the handler which can change the querystring 
                queryString = this.CheckQuerystringHandler(context, queryString, request.Unvalidated.RawUrl);

                if (string.IsNullOrWhiteSpace(requestPath))
                {
                    return null;
                }

                string parts = !string.IsNullOrWhiteSpace(urlParameters) ? "?" + urlParameters : string.Empty;
                string fullPath = string.Format("{0}{1}?{2}", requestPath, parts, queryString);
                object resourcePath;

                // More legacy support code.
                if (hasMultiParams)
                {
                    resourcePath = string.IsNullOrWhiteSpace(urlParameters)
                        ? new Uri(requestPath, UriKind.RelativeOrAbsolute)
                        : new Uri(requestPath + "?" + urlParameters, UriKind.RelativeOrAbsolute);
                }
                else
                {
                    resourcePath = requestPath;
                }

                // Check whether the path is valid for other requests.
                if (!currentService.IsValidRequest(resourcePath.ToString()))
                {
                    return null;
                }

                string combined = requestPath + fullPath + queryString;
                using (await Locker.LockAsync(combined))
                {
                    // Create a new cache to help process and cache the request.
                    this.imageCache = (IImageCache)ImageProcessorConfiguration.Instance
                        .ImageCache.GetInstance(requestPath, fullPath, queryString);

                    // Is the file new or updated?
                    bool isNewOrUpdated = await this.imageCache.IsNewOrUpdatedAsync();
                    string cachedPath = this.imageCache.CachedPath;

                    // Only process if the file has been updated.
                    if (isNewOrUpdated)
                    {
                        // Process the image.
                        bool exif = preserveExifMetaData != null && preserveExifMetaData.Value;
                        bool gamma = fixGamma != null && fixGamma.Value;
                        using (ImageFactory imageFactory = new ImageFactory(exif, gamma))
                        {
                            byte[] imageBuffer = await currentService.GetImage(resourcePath);
                            string mimeType;

                            using (MemoryStream inStream = new MemoryStream(imageBuffer))
                            {
                                // Process the Image
                                MemoryStream outStream = new MemoryStream();

                                if (!string.IsNullOrWhiteSpace(queryString))
                                {
                                    imageFactory.Load(inStream).AutoProcess(queryString).Save(outStream);
                                    mimeType = imageFactory.CurrentImageFormat.MimeType;
                                }
                                else
                                {
                                    await inStream.CopyToAsync(outStream);
                                    mimeType = FormatUtilities.GetFormat(outStream).MimeType;
                                }

                                // Post process the image.
                                if (ImageProcessorConfiguration.Instance.PostProcess)
                                {
                                    string extension = Path.GetExtension(cachedPath);
                                    outStream = await ImageProcessor.Web.PostProcessor.PostProcessor.PostProcessImageAsync(outStream, extension);
                                }

                                // Add to the cache.
                                await this.imageCache.AddImageToCacheAsync(outStream, mimeType);

                                // Cleanup
                                outStream.Dispose();
                            }

                            // Store the cached path, response type, and cache dependency in the context for later retrieval.
                            context.Items[CachedPathKey] = cachedPath;
                            context.Items[CachedResponseTypeKey] = mimeType;
                            bool isFileCached = new Uri(cachedPath).IsFile;

                            if (isFileLocal)
                            {
                                if (isFileCached)
                                {
                                    // Some services might only provide filename so we can't monitor for the browser.
                                    context.Items[CachedResponseFileDependency] = Path.GetFileName(requestPath) == requestPath
                                        ? new List<string> { cachedPath }
                                        : new List<string> { requestPath, cachedPath };
                                }
                                else
                                {
                                    context.Items[CachedResponseFileDependency] = Path.GetFileName(requestPath) == requestPath
                                        ? null
                                        : new List<string> { requestPath };
                                }
                            }
                            else if (isFileCached)
                            {
                                context.Items[CachedResponseFileDependency] = new List<string> { cachedPath };
                            }
                        }
                    }

                    // The cached file is valid so just rewrite the path.
                    this.imageCache.RewritePath(context);

                    // Redirect if not a locally store file.
                    //if (!new Uri(cachedPath).IsFile)
                    //{
                    //    context.ApplicationInstance.CompleteRequest();
                    //}
                    // Create a new cache to help process and cache the request.
                    return cachedPath;
                }
            }
            return null;
        }


        /// <summary>
        /// Replaces preset values stored in the configuration in the querystring.
        /// </summary>
        /// <param name="queryString">
        /// The query string.
        /// </param>
        /// <returns>
        /// The <see cref="string"/> containing the updated querystring.
        /// </returns>
        private string ReplacePresetsInQueryString(string queryString)
        {
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                foreach (Match match in PresetRegex.Matches(queryString))
                {
                    if (match.Success)
                    {
                        string preset = match.Value.Split('=')[1];

                        // We use the processor config system to store the preset values.
                        string replacements = ImageProcessorConfiguration.Instance.GetPresetSettings(preset);
                        queryString = Regex.Replace(queryString, match.Value, replacements ?? string.Empty);
                    }
                }
            }

            return queryString;
        }

        /// <summary>
        /// Checks if there is a handler that changes the querystring and executes that handler.
        /// </summary>
        /// <param name="context">The current request context.</param>
        /// <param name="queryString">The query string.</param>
        /// <param name="rawUrl">The raw request url.</param>
        /// <returns>
        /// The <see cref="string"/> containing the updated querystring.
        /// </returns>
        private string CheckQuerystringHandler(HttpContext context, string queryString, string rawUrl)
        {
            // Fire the process querystring event.
            ImageProcessor.Web.HttpModules.ImageProcessingModule.ProcessQuerystringEventHandler handler = OnProcessQuerystring;
            if (handler != null)
            {
                ProcessQueryStringEventArgs args = new ProcessQueryStringEventArgs
                {
                    Context = context,
                    Querystring = queryString ?? string.Empty,
                    RawUrl = rawUrl ?? string.Empty
                };
                queryString = handler(this, args);
            }

            return queryString;
        }
        #endregion 
    }
}