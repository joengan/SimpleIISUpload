using System;
using System.Linq;
using System.Net;
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

    public static class UploadRequestThrottle
    {
        private const string FailureCountCacheKeyPrefix = "ValidatePermissionFailureCount:";
        private const string LockoutUntilCacheKeyPrefix = "ValidatePermissionLockoutUntil:";
        private const string BlockedUntilCacheKeyPrefix = "UploadBlockedUntil:";
        private const int FailureLimit = 3;
        private const string ValidatePermissionLockoutMessage = "❌ 已連續錯誤3次，請1分鐘後再試";
        private const string BlockedMessage = "出現異常操作，此IP已被封鎖。";
        private static readonly TimeSpan ValidatePermissionLockoutDuration = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan AbnormalUploadBlockDuration = TimeSpan.FromHours(1);

        public static UploadRequestThrottleResult GetThrottleResult(HttpRequestBase request)
        {
            string clientIp = GetClientIpAddress(request);
            DateTime now = DateTime.UtcNow;
            object blockedUntil = HttpRuntime.Cache.Get(GetBlockedUntilCacheKey(clientIp));

            if (blockedUntil is DateTime && (DateTime)blockedUntil > now)
            {
                return new UploadRequestThrottleResult
                {
                    IsThrottled = true,
                    StatusCode = (int)HttpStatusCode.Forbidden,
                    Message = BlockedMessage
                };
            }

            object lockoutUntil = HttpRuntime.Cache.Get(GetLockoutUntilCacheKey(clientIp));
            if (lockoutUntil is DateTime && (DateTime)lockoutUntil > now)
            {
                return new UploadRequestThrottleResult
                {
                    IsThrottled = true,
                    StatusCode = 429,
                    Message = ValidatePermissionLockoutMessage
                };
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
            string clientIp = GetClientIpAddress(request);
            HttpRuntime.Cache.Remove(GetFailureCountCacheKey(clientIp));
            HttpRuntime.Cache.Remove(GetLockoutUntilCacheKey(clientIp));
        }

        public static void RegisterValidatePermissionFailure(HttpRequestBase request)
        {
            string clientIp = GetClientIpAddress(request);
            if (IsBlocked(clientIp))
            {
                return;
            }

            string failureCountKey = GetFailureCountCacheKey(clientIp);
            int failureCount = HttpRuntime.Cache.Get(failureCountKey) as int? ?? 0;
            failureCount++;

            DateTime failureCountExpiresAt = DateTime.UtcNow.Add(ValidatePermissionLockoutDuration);
            HttpRuntime.Cache.Insert(
                failureCountKey,
                failureCount,
                null,
                failureCountExpiresAt,
                Cache.NoSlidingExpiration);

            if (failureCount < FailureLimit)
            {
                return;
            }

            DateTime lockoutUntil = DateTime.UtcNow.Add(ValidatePermissionLockoutDuration);
            HttpRuntime.Cache.Insert(
                GetLockoutUntilCacheKey(clientIp),
                lockoutUntil,
                null,
                lockoutUntil,
                Cache.NoSlidingExpiration);

            HttpRuntime.Cache.Remove(failureCountKey);
        }

        public static UploadRequestThrottleResult BlockAbnormalUpload(HttpRequestBase request)
        {
            string clientIp = GetClientIpAddress(request);
            DateTime blockedUntil = DateTime.UtcNow.Add(AbnormalUploadBlockDuration);

            HttpRuntime.Cache.Insert(
                GetBlockedUntilCacheKey(clientIp),
                blockedUntil,
                null,
                blockedUntil,
                Cache.NoSlidingExpiration);

            HttpRuntime.Cache.Remove(GetFailureCountCacheKey(clientIp));
            HttpRuntime.Cache.Remove(GetLockoutUntilCacheKey(clientIp));

            return new UploadRequestThrottleResult
            {
                IsThrottled = true,
                StatusCode = (int)HttpStatusCode.Forbidden,
                Message = BlockedMessage
            };
        }

        private static bool IsBlocked(string clientIp)
        {
            object blockedUntil = HttpRuntime.Cache.Get(GetBlockedUntilCacheKey(clientIp));
            return blockedUntil is DateTime && (DateTime)blockedUntil > DateTime.UtcNow;
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

        private static string GetClientIpAddress(HttpRequestBase request)
        {
            string forwardedFor = request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                return forwardedFor
                    .Split(',')
                    .Select(ip => ip.Trim())
                    .FirstOrDefault(ip => ip.Length > 0);
            }

            string realIp = request.Headers["X-Real-IP"];
            if (!string.IsNullOrWhiteSpace(realIp))
            {
                return realIp.Trim();
            }

            return request.UserHostAddress ?? string.Empty;
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
