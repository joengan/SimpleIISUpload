using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Caching;
using System.Web.Mvc;

namespace SimpleIISUpload.Filters
{
    public sealed class UploadRequestThrottleResult
    {
        public bool IsThrottled { get; set; }
        public int StatusCode { get; set; }
        public string Message { get; set; }
    }

    public static class UploadSecurityEventLogger
    {
        private const string CustomEventLogName = "SimpleIISUpload";
        private const string CustomEventLogSource = "SimpleIISUpload.Security";
        private const string FallbackEventLogName = "Application";
        private const string FallbackEventLogSource = "SimpleIISUpload";
        private const string LastResortEventLogSource = "ASP.NET 4.0.30319.0";
        private const int SecurityEventId = 41001;
        private const int MaxEventMessageLength = 30000;
        private static readonly object EventLogSyncRoot = new object();

        public static void LogSecurityEvent(HttpRequestBase request, string eventType, string rule, int statusCode, string message, NameValueCollection extraData = null)
        {
            if (request == null)
            {
                return;
            }

            string eventMessage = BuildEventMessage(request, eventType, rule, statusCode, message, extraData);

            try
            {
                EnsureCustomEventSource();
                EventLog.WriteEntry(CustomEventLogSource, eventMessage, EventLogEntryType.Warning, SecurityEventId);
            }
            catch
            {
                WriteToFallbackLogs(eventMessage);
            }
        }

        private static string BuildEventMessage(HttpRequestBase request, string eventType, string rule, int statusCode, string message, NameValueCollection extraData)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("SimpleIISUpload security event");
            builder.AppendLine("EventType: " + SafeValue(eventType));
            builder.AppendLine("Rule: " + SafeValue(rule));
            builder.AppendLine("StatusCode: " + statusCode);
            builder.AppendLine("Message: " + SafeValue(message));
            builder.AppendLine("DetectedUtc: " + DateTime.UtcNow.ToString("o"));
            builder.AppendLine("DetectedLocal: " + DateTime.Now.ToString("o"));
            builder.AppendLine("ServerMachineName: " + Environment.MachineName);
            builder.AppendLine("AppDomain: " + AppDomain.CurrentDomain.FriendlyName);
            builder.AppendLine("HttpMethod: " + SafeValue(request.HttpMethod));
            builder.AppendLine("RawUrl: " + SafeValue(request.RawUrl));
            builder.AppendLine("Url: " + SafeValue(request.Url == null ? null : request.Url.AbsoluteUri));
            builder.AppendLine("UrlReferrer: " + SafeValue(request.UrlReferrer == null ? null : request.UrlReferrer.AbsoluteUri));
            builder.AppendLine("HostHeader: " + SafeValue(request.Headers["Host"]));
            builder.AppendLine("UserHostAddress: " + SafeValue(request.UserHostAddress));
            builder.AppendLine("UserHostName: " + SafeValue(request.UserHostName));
            builder.AppendLine("ClientIpAddresses: " + string.Join(", ", UploadRequestThrottle.GetClientIpAddresses(request)));
            builder.AppendLine("ContentType: " + SafeValue(request.ContentType));
            builder.AppendLine("ContentLength: " + request.ContentLength);
            builder.AppendLine("UserAgent: " + SafeValue(request.UserAgent));
            builder.AppendLine("IsAuthenticated: " + (request.IsAuthenticated ? "true" : "false"));
            builder.AppendLine("IsSecureConnection: " + (request.IsSecureConnection ? "true" : "false"));
            builder.AppendLine("UserLanguages: " + string.Join(", ", request.UserLanguages ?? new string[0]));
            builder.AppendLine("RemoteAddr: " + SafeValue(request.ServerVariables["REMOTE_ADDR"]));
            builder.AppendLine("RemoteHost: " + SafeValue(request.ServerVariables["REMOTE_HOST"]));
            builder.AppendLine("ForwardedFor: " + SafeValue(request.ServerVariables["HTTP_X_FORWARDED_FOR"]));
            builder.AppendLine("XRealIp: " + SafeValue(request.ServerVariables["HTTP_X_REAL_IP"]));

            AppendCollection(builder, "Headers", request.Headers);
            AppendCollection(builder, "ServerVariables", request.ServerVariables, "ALL_HTTP", "ALL_RAW", "HTTP_COOKIE");
            AppendCollection(builder, "QueryString", request.QueryString);
            AppendCollection(builder, "Form", request.Form);
            AppendFiles(builder, request);
            AppendCollection(builder, "ExtraData", extraData);

            string fullMessage = builder.ToString();
            if (fullMessage.Length <= MaxEventMessageLength)
            {
                return fullMessage;
            }

            return fullMessage.Substring(0, MaxEventMessageLength) + Environment.NewLine + "[Message truncated]";
        }

        private static void AppendCollection(StringBuilder builder, string title, NameValueCollection values, params string[] excludedKeys)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            builder.AppendLine(title + ":");
            foreach (string key in values.AllKeys.Where(key => key != null))
            {
                if (excludedKeys != null && excludedKeys.Any(excludedKey => string.Equals(excludedKey, key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                builder.AppendLine("  " + key + ": " + SafeValue(values[key]));
            }
        }

        private static void AppendFiles(StringBuilder builder, HttpRequestBase request)
        {
            if (request.Files == null || request.Files.Count == 0)
            {
                return;
            }

            builder.AppendLine("Files:");
            for (int i = 0; i < request.Files.Count; i++)
            {
                HttpPostedFileBase file = request.Files[i];
                if (file == null)
                {
                    continue;
                }

                builder.AppendLine("  Key: " + SafeValue(request.Files.AllKeys[i]));
                builder.AppendLine("    FileName: " + SafeValue(file.FileName));
                builder.AppendLine("    ContentType: " + SafeValue(file.ContentType));
                builder.AppendLine("    ContentLength: " + file.ContentLength);
            }
        }

        private static string SafeValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
        }

        private static void EnsureCustomEventSource()
        {
            if (EventLog.SourceExists(CustomEventLogSource))
            {
                return;
            }

            lock (EventLogSyncRoot)
            {
                if (!EventLog.SourceExists(CustomEventLogSource))
                {
                    EventSourceCreationData sourceData = new EventSourceCreationData(CustomEventLogSource, CustomEventLogName);
                    EventLog.CreateEventSource(sourceData);
                }
            }
        }

        private static void WriteToFallbackLogs(string eventMessage)
        {
            try
            {
                EnsureFallbackEventSource();
                EventLog.WriteEntry(FallbackEventLogSource, eventMessage, EventLogEntryType.Warning, SecurityEventId);
            }
            catch
            {
                try
                {
                    EventLog.WriteEntry(LastResortEventLogSource, eventMessage, EventLogEntryType.Warning, SecurityEventId);
                }
                catch
                {
                }
            }
        }

        private static void EnsureFallbackEventSource()
        {
            if (EventLog.SourceExists(FallbackEventLogSource))
            {
                return;
            }

            lock (EventLogSyncRoot)
            {
                if (!EventLog.SourceExists(FallbackEventLogSource))
                {
                    EventSourceCreationData sourceData = new EventSourceCreationData(FallbackEventLogSource, FallbackEventLogName);
                    EventLog.CreateEventSource(sourceData);
                }
            }
        }
    }

    public static class UploadRequestThrottle
    {
        private const string FailureCountCacheKeyPrefix = "ValidatePermissionFailureCount:";
        private const string LockoutUntilCacheKeyPrefix = "ValidatePermissionLockoutUntil:";
        private const string BlockedUntilCacheKeyPrefix = "UploadBlockedUntil:";
        private const string SecurityLogCacheKeyPrefix = "UploadSecurityLog:";
        private const int FailureLimit = 3;
        private const string ValidatePermissionLockoutMessage = "❌ 已連續錯誤3次，請1分鐘後再試";
        private const string BlockedMessage = "出現異常操作，此IP已被封鎖。";
        private static readonly TimeSpan ValidatePermissionLockoutDuration = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan AbnormalUploadBlockDuration = TimeSpan.FromHours(1);
        private static readonly TimeSpan SecurityLogThrottleDuration = TimeSpan.FromMinutes(5);

        public static UploadRequestThrottleResult GetThrottleResult(HttpRequestBase request)
        {
            string[] clientIps = GetClientIpAddresses(request);
            DateTime now = DateTime.UtcNow;
            if (clientIps.Any(clientIp => IsBlocked(clientIp, now)))
            {
                UploadRequestThrottleResult blockedResult = new UploadRequestThrottleResult
                {
                    IsThrottled = true,
                    StatusCode = (int)HttpStatusCode.Forbidden,
                    Message = BlockedMessage
                };

                LogThrottledRequest(request, "BlockedRequestRejected", blockedResult.StatusCode, blockedResult.Message);
                return blockedResult;
            }

            if (clientIps.Any(clientIp => IsLockedOut(clientIp, now)))
            {
                UploadRequestThrottleResult lockedResult = new UploadRequestThrottleResult
                {
                    IsThrottled = true,
                    StatusCode = 429,
                    Message = ValidatePermissionLockoutMessage
                };

                LogThrottledRequest(request, "ValidatePermissionLockedRequestRejected", lockedResult.StatusCode, lockedResult.Message);
                return lockedResult;
            }

            return new UploadRequestThrottleResult
            {
                IsThrottled = false,
                StatusCode = (int)HttpStatusCode.OK,
                Message = string.Empty
            };
        }

        public static void RegisterValidatePermissionSuccess(HttpRequestBase request)
        {
            foreach (string clientIp in GetClientIpAddresses(request))
            {
                HttpRuntime.Cache.Remove(GetFailureCountCacheKey(clientIp));
                HttpRuntime.Cache.Remove(GetLockoutUntilCacheKey(clientIp));
            }
        }

        public static void RegisterValidatePermissionFailure(HttpRequestBase request)
        {
            string[] clientIps = GetClientIpAddresses(request);
            if (clientIps.Any(IsBlocked))
            {
                return;
            }

            DateTime failureCountExpiresAt = DateTime.UtcNow.Add(ValidatePermissionLockoutDuration);
            bool shouldLockout = false;

            foreach (string clientIp in clientIps)
            {
                string failureCountKey = GetFailureCountCacheKey(clientIp);
                int failureCount = HttpRuntime.Cache.Get(failureCountKey) as int? ?? 0;
                failureCount++;

                HttpRuntime.Cache.Insert(
                    failureCountKey,
                    failureCount,
                    null,
                    failureCountExpiresAt,
                    Cache.NoSlidingExpiration);

                if (failureCount >= FailureLimit)
                {
                    shouldLockout = true;
                }
            }

            if (!shouldLockout)
            {
                return;
            }

            DateTime lockoutUntil = DateTime.UtcNow.Add(ValidatePermissionLockoutDuration);
            foreach (string clientIp in clientIps)
            {
                HttpRuntime.Cache.Insert(
                    GetLockoutUntilCacheKey(clientIp),
                    lockoutUntil,
                    null,
                    lockoutUntil,
                    Cache.NoSlidingExpiration);

                HttpRuntime.Cache.Remove(GetFailureCountCacheKey(clientIp));
            }

            UploadSecurityEventLogger.LogSecurityEvent(
                request,
                "ValidatePermissionLockout",
                "ValidatePermissionFailureLimitExceeded",
                429,
                ValidatePermissionLockoutMessage,
                new NameValueCollection
                {
                    { "FailureLimit", FailureLimit.ToString() },
                    { "LockoutDurationMinutes", ValidatePermissionLockoutDuration.TotalMinutes.ToString("0") },
                    { "ClientIpCount", clientIps.Length.ToString() }
                });
        }

        public static UploadRequestThrottleResult BlockAbnormalUpload(HttpRequestBase request)
        {
            return BlockAbnormalUpload(request, "AbnormalUploadBlocked");
        }

        public static UploadRequestThrottleResult BlockAbnormalUpload(HttpRequestBase request, string rule)
        {
            string[] clientIps = GetClientIpAddresses(request);
            DateTime blockedUntil = DateTime.UtcNow.Add(AbnormalUploadBlockDuration);

            foreach (string clientIp in clientIps)
            {
                HttpRuntime.Cache.Insert(
                    GetBlockedUntilCacheKey(clientIp),
                    blockedUntil,
                    null,
                    blockedUntil,
                    Cache.NoSlidingExpiration);

                HttpRuntime.Cache.Remove(GetFailureCountCacheKey(clientIp));
                HttpRuntime.Cache.Remove(GetLockoutUntilCacheKey(clientIp));
            }

            UploadSecurityEventLogger.LogSecurityEvent(
                request,
                "AbnormalUploadBlocked",
                rule,
                (int)HttpStatusCode.Forbidden,
                BlockedMessage,
                new NameValueCollection
                {
                    { "BlockedDurationHours", AbnormalUploadBlockDuration.TotalHours.ToString("0") },
                    { "ClientIpCount", clientIps.Length.ToString() }
                });

            return new UploadRequestThrottleResult
            {
                IsThrottled = true,
                StatusCode = (int)HttpStatusCode.Forbidden,
                Message = BlockedMessage
            };
        }

        private static bool IsBlocked(string clientIp)
        {
            return IsBlocked(clientIp, DateTime.UtcNow);
        }

        private static bool IsBlocked(string clientIp, DateTime now)
        {
            object blockedUntil = HttpRuntime.Cache.Get(GetBlockedUntilCacheKey(clientIp));
            return blockedUntil is DateTime && (DateTime)blockedUntil > now;
        }

        private static bool IsLockedOut(string clientIp, DateTime now)
        {
            object lockoutUntil = HttpRuntime.Cache.Get(GetLockoutUntilCacheKey(clientIp));
            return lockoutUntil is DateTime && (DateTime)lockoutUntil > now;
        }

        private static string GetFailureCountCacheKey(string clientIp)
        {
            return FailureCountCacheKeyPrefix + NormalizeClientIp(clientIp);
        }

        private static string GetLockoutUntilCacheKey(string clientIp)
        {
            return LockoutUntilCacheKeyPrefix + NormalizeClientIp(clientIp);
        }

        private static string GetBlockedUntilCacheKey(string clientIp)
        {
            return BlockedUntilCacheKeyPrefix + NormalizeClientIp(clientIp);
        }

        private static string NormalizeClientIp(string clientIp)
        {
            return string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp;
        }

        internal static string[] GetClientIpAddresses(HttpRequestBase request)
        {
            string forwardedFor = request.Headers["X-Forwarded-For"];
            string[] clientIps = (string.IsNullOrWhiteSpace(forwardedFor)
                    ? Enumerable.Empty<string>()
                    : forwardedFor
                        .Split(',')
                        .Select(ip => ip.Trim())
                        .Where(ip => ip.Length > 0))
                .Concat(new[]
                {
                    request.Headers["X-Real-IP"],
                    request.UserHostAddress
                })
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Select(ip => ip.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return clientIps.Length > 0 ? clientIps : new[] { string.Empty };
        }

        private static void LogThrottledRequest(HttpRequestBase request, string rule, int statusCode, string message)
        {
            string logKey = GetSecurityLogCacheKey(rule, GetClientIpAddresses(request));
            if (HttpRuntime.Cache.Get(logKey) != null)
            {
                return;
            }

            DateTime expiresAt = DateTime.UtcNow.Add(SecurityLogThrottleDuration);
            HttpRuntime.Cache.Insert(logKey, true, null, expiresAt, Cache.NoSlidingExpiration);

            UploadSecurityEventLogger.LogSecurityEvent(
                request,
                "ThrottledRequestRejected",
                rule,
                statusCode,
                message,
                new NameValueCollection
                {
                    { "LogThrottleMinutes", SecurityLogThrottleDuration.TotalMinutes.ToString("0") }
                });
        }

        private static string GetSecurityLogCacheKey(string rule, string[] clientIps)
        {
            string normalizedIps = string.Join("|", clientIps
                .Select(NormalizeClientIp)
                .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase));

            return SecurityLogCacheKeyPrefix + rule + ":" + normalizedIps;
        }
    }

    public static class UploadClientIpAccessControl
    {
        private const string AllowedClientIpsSettingKey = "AllowedClientIps";
        private const string AllowedClientIpLogCacheKeyPrefix = "AllowedClientIpDenied:";
        public const string AccessDeniedMessage = "拒絕訪問：目前來源 IP 不在允許清單內。";
        private static readonly TimeSpan AccessDeniedLogThrottleDuration = TimeSpan.FromMinutes(5);

        public static bool IsRequestAllowed(HttpRequestBase request)
        {
            string[] configuredEntries = GetConfiguredAllowedClientIpEntries();
            if (configuredEntries.Length == 0)
            {
                return true;
            }

            HashSet<string> allowedClientIps = new HashSet<string>(
                configuredEntries
                    .Select(TryNormalizeIpAddress)
                    .Where(ip => ip != null),
                StringComparer.OrdinalIgnoreCase);

            string[] requestClientIps = UploadRequestThrottle.GetClientIpAddresses(request)
                .Select(TryNormalizeIpAddress)
                .Where(ip => ip != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (requestClientIps.Any(clientIp => allowedClientIps.Contains(clientIp)))
            {
                return true;
            }

            LogDeniedRequest(request, configuredEntries.Length, allowedClientIps.Count);
            return false;
        }

        private static string[] GetConfiguredAllowedClientIpEntries()
        {
            string configuredValue = ConfigurationManager.AppSettings[AllowedClientIpsSettingKey] ?? string.Empty;
            return configuredValue
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(ip => ip.Trim())
                .Where(ip => ip.Length > 0)
                .ToArray();
        }

        private static string TryNormalizeIpAddress(string value)
        {
            IPAddress ipAddress;
            if (!IPAddress.TryParse(value, out ipAddress))
            {
                return null;
            }

            return NormalizeIpAddress(ipAddress).ToString();
        }

        private static IPAddress NormalizeIpAddress(IPAddress ipAddress)
        {
            return ipAddress != null && ipAddress.IsIPv4MappedToIPv6
                ? ipAddress.MapToIPv4()
                : ipAddress;
        }

        private static void LogDeniedRequest(HttpRequestBase request, int configuredEntryCount, int validConfiguredEntryCount)
        {
            string[] clientIps = UploadRequestThrottle.GetClientIpAddresses(request);
            string logKey = AllowedClientIpLogCacheKeyPrefix + string.Join("|", clientIps
                .Select(ip => string.IsNullOrWhiteSpace(ip) ? "unknown" : ip.Trim())
                .OrderBy(ip => ip, StringComparer.OrdinalIgnoreCase));

            if (HttpRuntime.Cache.Get(logKey) != null)
            {
                return;
            }

            DateTime expiresAt = DateTime.UtcNow.Add(AccessDeniedLogThrottleDuration);
            HttpRuntime.Cache.Insert(logKey, true, null, expiresAt, Cache.NoSlidingExpiration);

            UploadSecurityEventLogger.LogSecurityEvent(
                request,
                "ClientIpAccessDenied",
                AllowedClientIpsSettingKey,
                (int)HttpStatusCode.Forbidden,
                AccessDeniedMessage,
                new NameValueCollection
                {
                    { "ConfiguredEntryCount", configuredEntryCount.ToString() },
                    { "ValidConfiguredEntryCount", validConfiguredEntryCount.ToString() },
                    { "LogThrottleMinutes", AccessDeniedLogThrottleDuration.TotalMinutes.ToString("0") }
                });
        }
    }

    public sealed class AllowedClientIpRestrictionAttribute : ActionFilterAttribute
    {
        private const string AccessDeniedPageVirtualPath = "~/Content/AccessDenied.html";

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            if (UploadClientIpAccessControl.IsRequestAllowed(filterContext.HttpContext.Request))
            {
                return;
            }

            filterContext.HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            filterContext.HttpContext.Response.TrySkipIisCustomErrors = true;
            filterContext.HttpContext.Response.ContentType = "text/html";
            filterContext.HttpContext.Response.ContentEncoding = Encoding.UTF8;
            filterContext.HttpContext.Response.Charset = "utf-8";
            filterContext.Result = new ContentResult
            {
                Content = ReadAccessDeniedPage(filterContext.HttpContext),
                ContentEncoding = Encoding.UTF8,
                ContentType = "text/html"
            };
        }

        private static string ReadAccessDeniedPage(HttpContextBase httpContext)
        {
            string physicalPath = httpContext.Server.MapPath(AccessDeniedPageVirtualPath);
            if (!File.Exists(physicalPath))
            {
                return "<!DOCTYPE html><html lang=\"zh-Hant\"><head><meta charset=\"utf-8\" /><title>拒絕訪問</title></head><body><h1>拒絕訪問</h1><p>目前來源 IP 不在允許清單內。</p></body></html>";
            }

            return File.ReadAllText(physicalPath, Encoding.UTF8);
        }
    }

    public sealed class ValidatePermissionThrottleAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            UploadRequestThrottleResult throttle = UploadRequestThrottle.GetThrottleResult(filterContext.HttpContext.Request);
            if (throttle.IsThrottled)
            {
                filterContext.HttpContext.Response.StatusCode = throttle.StatusCode;
                filterContext.HttpContext.Response.TrySkipIisCustomErrors = true;
                filterContext.Result = new ContentResult { Content = throttle.Message };
            }
        }

        public override void OnActionExecuted(ActionExecutedContext filterContext)
        {
            if (filterContext.Exception != null)
            {
                return;
            }

            if (filterContext.HttpContext.Response.StatusCode == (int)System.Net.HttpStatusCode.OK)
            {
                UploadRequestThrottle.RegisterValidatePermissionSuccess(filterContext.HttpContext.Request);
                return;
            }

            UploadRequestThrottle.RegisterValidatePermissionFailure(filterContext.HttpContext.Request);
        }
    }
}



