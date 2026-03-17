using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;
using Newtonsoft.Json;
using SimpleIISUpload.Filters;

namespace SimpleIISUpload.Controllers
{
    public class UploadController : Controller
    {
        private const string UploadPermissionSessionKey = "UploadPermission";
        private const string UploadPasswordSessionKey = "UploadPassword";
        private const string UploadStateFileName = "upload.json";
        private const string UploadTempFileName = "upload.tmp";
        private const long DefaultMaxUploadFileBytes = 8L * 1024 * 1024 * 1024;
        private const int DefaultChunkSizeBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan UploadSessionRetention = TimeSpan.FromDays(2);

        private enum UploadPermission
        {
            None,
            Limited,
            Overwrite
        }

        private sealed class PasswordValidationResult
        {
            public bool IsSuccess { get; set; }
            public int StatusCode { get; set; }
            public string Message { get; set; }
            public UploadPermission Permission { get; set; }
        }

        private sealed class UploadSessionState
        {
            public string UploadId { get; set; }
            public string OriginalFileName { get; set; }
            public string StoredFileName { get; set; }
            public string Mode { get; set; }
            public long FileSize { get; set; }
            public int ChunkSize { get; set; }
            public long UploadedBytes { get; set; }
            public DateTime LastUpdatedUtc { get; set; }
        }

        [HttpGet]
        public ActionResult Index() => View();

        [HttpPost]
        [ValidatePermissionThrottle]
        public ActionResult ValidatePermission(string pwd, string mode)
        {
            PasswordValidationResult validation = ValidatePasswordAndMode(pwd, mode);
            if (!validation.IsSuccess)
            {
                ClearUploadValidation();
                SetResponseStatus(validation.StatusCode);
                return Content(validation.Message);
            }

            Session[UploadPermissionSessionKey] = validation.Permission.ToString();
            Session[UploadPasswordSessionKey] = pwd;
            return Content("OK");
        }

        [HttpPost]
        public ActionResult InitializeUpload(string uploadId, string fileName, long fileSize, string pwd, string mode)
        {
            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode))
            {
                ClearUploadValidation();
                UploadRequestThrottleResult throttle = UploadRequestThrottle.BlockAbnormalUpload(Request);
                return JsonError(throttle.StatusCode, throttle.Message);
            }

            string normalizedUploadId = NormalizeUploadId(uploadId);
            if (normalizedUploadId == null)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳識別碼錯誤");
            }

            string safeFileName = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 檔名錯誤");
            }

            if (fileSize <= 0)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 請選擇檔案");
            }

            long maxUploadFileBytes = GetConfiguredMaxUploadFileBytes();
            if (fileSize > maxUploadFileBytes)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 檔案超過 8GB 上限");
            }

            CleanupExpiredUploadSessions();

            UploadSessionState state = LoadUploadSessionState(normalizedUploadId);
            if (state != null)
            {
                if (!string.Equals(state.OriginalFileName, safeFileName, StringComparison.Ordinal)
                    || state.FileSize != fileSize
                    || !string.Equals(state.Mode, mode, StringComparison.Ordinal))
                {
                    DeleteUploadSession(normalizedUploadId);
                    state = null;
                }
                else
                {
                    state.UploadedBytes = GetUploadedBytes(state.UploadId, state.FileSize);
                    SaveUploadSessionState(state);
                    return Json(new
                    {
                        uploadId = state.UploadId,
                        fileName = state.StoredFileName,
                        uploadedBytes = state.UploadedBytes,
                        chunkSize = state.ChunkSize,
                        maxFileSize = maxUploadFileBytes,
                        isResuming = state.UploadedBytes > 0
                    });
                }
            }

            string storedFileName;
            string errorMessage;
            if (!TryResolveTargetFileName(GetUploadFolderPath(), safeFileName, mode, out storedFileName, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.Conflict, errorMessage);
            }

            state = new UploadSessionState
            {
                UploadId = normalizedUploadId,
                OriginalFileName = safeFileName,
                StoredFileName = storedFileName,
                Mode = mode,
                FileSize = fileSize,
                ChunkSize = GetConfiguredChunkSizeBytes(),
                UploadedBytes = 0,
                LastUpdatedUtc = DateTime.UtcNow
            };

            SaveUploadSessionState(state);

            return Json(new
            {
                uploadId = state.UploadId,
                fileName = state.StoredFileName,
                uploadedBytes = state.UploadedBytes,
                chunkSize = state.ChunkSize,
                maxFileSize = maxUploadFileBytes,
                isResuming = false
            });
        }

        [HttpPost]
        public ActionResult UploadChunk(string uploadId, int chunkIndex, int totalChunks, long chunkStart, string pwd, string mode, HttpPostedFileBase chunk)
        {
            UploadRequestThrottleResult throttle = UploadRequestThrottle.GetThrottleResult(Request);
            if (throttle.IsThrottled)
            {
                ClearUploadValidation();
                return JsonError(throttle.StatusCode, throttle.Message);
            }

            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode))
            {
                ClearUploadValidation();
                throttle = UploadRequestThrottle.BlockAbnormalUpload(Request);
                return JsonError(throttle.StatusCode, throttle.Message);
            }

            string normalizedUploadId = NormalizeUploadId(uploadId);
            if (normalizedUploadId == null)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳識別碼錯誤");
            }

            if (chunk == null || chunk.ContentLength == 0)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 缺少分段資料");
            }

            if (chunkIndex < 0 || totalChunks <= 0 || chunkStart < 0)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 分段資訊錯誤");
            }

            UploadSessionState state = LoadUploadSessionState(normalizedUploadId);
            if (state == null)
            {
                return JsonError((int)HttpStatusCode.NotFound, "❌ 找不到續傳資訊，請重新開始上傳");
            }

            if (!string.Equals(state.Mode, mode, StringComparison.Ordinal))
            {
                return JsonError((int)HttpStatusCode.Conflict, "❌ 上傳模式與續傳資訊不一致");
            }

            long currentLength = GetUploadedBytes(state.UploadId, state.FileSize);
            if (chunkStart < currentLength)
            {
                return Json(new
                {
                    uploadedBytes = currentLength,
                    message = "OK",
                    isCompleted = currentLength >= state.FileSize
                });
            }

            if (chunkStart > currentLength)
            {
                return JsonError((int)HttpStatusCode.Conflict, "❌ 上傳進度不同步，請重新續傳");
            }

            string tempFilePath = GetUploadTempFilePath(state.UploadId);
            Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath));

            using (FileStream stream = new FileStream(tempFilePath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                chunk.InputStream.CopyTo(stream);
            }

            currentLength = GetUploadedBytes(state.UploadId, state.FileSize);
            if (currentLength > state.FileSize)
            {
                DeleteUploadSession(state.UploadId);
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳內容超出檔案大小，請重新開始");
            }

            state.UploadedBytes = currentLength;
            SaveUploadSessionState(state);

            return Json(new
            {
                uploadedBytes = state.UploadedBytes,
                message = "OK",
                isCompleted = state.UploadedBytes >= state.FileSize
            });
        }

        [HttpPost]
        public ActionResult CompleteUpload(string uploadId, string pwd, string mode)
        {
            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode))
            {
                ClearUploadValidation();
                UploadRequestThrottleResult throttle = UploadRequestThrottle.BlockAbnormalUpload(Request);
                return JsonError(throttle.StatusCode, throttle.Message);
            }

            string normalizedUploadId = NormalizeUploadId(uploadId);
            if (normalizedUploadId == null)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳識別碼錯誤");
            }

            UploadSessionState state = LoadUploadSessionState(normalizedUploadId);
            if (state == null)
            {
                return JsonError((int)HttpStatusCode.NotFound, "❌ 找不到續傳資訊，請重新開始上傳");
            }

            string tempFilePath = GetUploadTempFilePath(state.UploadId);
            if (!System.IO.File.Exists(tempFilePath))
            {
                DeleteUploadSession(state.UploadId);
                return JsonError((int)HttpStatusCode.NotFound, "❌ 找不到上傳暫存檔");
            }

            long uploadedBytes = GetUploadedBytes(state.UploadId, state.FileSize);
            if (uploadedBytes != state.FileSize)
            {
                state.UploadedBytes = uploadedBytes;
                SaveUploadSessionState(state);
                return JsonError((int)HttpStatusCode.Conflict, "❌ 檔案尚未完整上傳，請繼續續傳");
            }

            string finalFileName;
            string errorMessage;
            if (!TryResolveCompletionFileName(GetUploadFolderPath(), state, out finalFileName, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.Conflict, errorMessage);
            }

            string finalPath = Path.Combine(GetUploadFolderPath(), finalFileName);
            if (string.Equals(state.Mode, "overwrite", StringComparison.Ordinal) && System.IO.File.Exists(finalPath))
            {
                System.IO.File.Delete(finalPath);
            }

            System.IO.File.Move(tempFilePath, finalPath);
            DeleteUploadSession(state.UploadId);
            ClearUploadValidation();
            return Json(new { message = $"✅ 成功上傳: {finalFileName}" });
        }

        [HttpPost]
        public ActionResult DoUpload(HttpPostedFileBase file, string pwd, string mode)
        {
            UploadRequestThrottleResult throttle = UploadRequestThrottle.GetThrottleResult(Request);
            if (throttle.IsThrottled)
            {
                ClearUploadValidation();
                SetResponseStatus(throttle.StatusCode);
                return Content(throttle.Message);
            }

            if (file == null || file.ContentLength == 0)
            {
                SetResponseStatus((int)HttpStatusCode.BadRequest);
                return Content("❌ 請選擇檔案");
            }

            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                SetResponseStatus((int)HttpStatusCode.BadRequest);
                return Content("❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode))
            {
                ClearUploadValidation();
                throttle = UploadRequestThrottle.BlockAbnormalUpload(Request);
                SetResponseStatus(throttle.StatusCode);
                return Content(throttle.Message);
            }

            // 2. 取得路徑
            string folder = ConfigurationManager.AppSettings["UploadPath"];
            if (folder.StartsWith("~")) folder = Server.MapPath(folder);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string fileName = Path.GetFileName(file.FileName);
            string fullPath = Path.Combine(folder, fileName);

            // 3. 處理重複邏輯
            if (System.IO.File.Exists(fullPath))
            {
                if (mode == "prohibit")
                {
                    ClearUploadValidation();
                    SetResponseStatus((int)HttpStatusCode.Conflict);
                    return Content("❌ 檔案已存在");
                }

                if (mode == "auto")
                {
                    string ext = Path.GetExtension(fileName);
                    string nameOnly = Path.GetFileNameWithoutExtension(fileName);
                    int v = 2;
                    while (System.IO.File.Exists(fullPath))
                    {
                        fileName = $"{nameOnly}_v{v}{ext}";
                        fullPath = Path.Combine(folder, fileName);
                        v++;
                    }
                }
                // Overwrite 模式則直接覆蓋
            }

            file.SaveAs(fullPath);
            ClearUploadValidation();
            return Content($"✅ 成功上傳: {fileName}");
        }

        private PasswordValidationResult ValidatePasswordAndMode(string pwd, string mode)
        {
            if (!IsSupportedMode(mode))
            {
                return new PasswordValidationResult
                {
                    IsSuccess = false,
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Message = "❌ 上傳模式錯誤",
                    Permission = UploadPermission.None
                };
            }

            string[] overwritePasswords = GetPasswords("OverwritePasswords");
            if (overwritePasswords.Contains(pwd))
            {
                return new PasswordValidationResult
                {
                    IsSuccess = true,
                    StatusCode = (int)HttpStatusCode.OK,
                    Message = "OK",
                    Permission = UploadPermission.Overwrite
                };
            }

            string[] limitedPasswords = GetPasswords("LimitedPasswords");
            if (limitedPasswords.Contains(pwd))
            {
                if (mode == "overwrite")
                {
                    return new PasswordValidationResult
                    {
                        IsSuccess = false,
                        StatusCode = (int)HttpStatusCode.Forbidden,
                        Message = "❌ 此密碼無覆寫權限，請改用禁止重複或自動編號上傳",
                        Permission = UploadPermission.Limited
                    };
                }

                return new PasswordValidationResult
                {
                    IsSuccess = true,
                    StatusCode = (int)HttpStatusCode.OK,
                    Message = "OK",
                    Permission = UploadPermission.Limited
                };
            }

            return new PasswordValidationResult
            {
                IsSuccess = false,
                StatusCode = (int)HttpStatusCode.Unauthorized,
                Message = "❌ 密碼錯誤",
                Permission = UploadPermission.None
            };
        }

        private bool IsSupportedMode(string mode)
        {
            return mode == "prohibit" || mode == "overwrite" || mode == "auto";
        }

        private string[] GetPasswords(string key)
        {
            string value = ConfigurationManager.AppSettings[key] ?? string.Empty;
            return value
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
        }

        private bool HasValidatedUploadSession(string pwd, string mode)
        {
            string sessionPassword = Session[UploadPasswordSessionKey] as string;
            string sessionPermission = Session[UploadPermissionSessionKey] as string;
            UploadPermission permission;

            if (!string.Equals(sessionPassword, pwd, StringComparison.Ordinal)
                || !Enum.TryParse(sessionPermission, out permission)
                || permission == UploadPermission.None)
            {
                return false;
            }

            return mode != "overwrite" || permission == UploadPermission.Overwrite;
        }

        private long GetConfiguredMaxUploadFileBytes()
        {
            return GetConfiguredLong("MaxUploadFileBytes", DefaultMaxUploadFileBytes);
        }

        private int GetConfiguredChunkSizeBytes()
        {
            long configured = GetConfiguredLong("UploadChunkSizeBytes", DefaultChunkSizeBytes);
            if (configured <= 0 || configured > int.MaxValue)
            {
                return DefaultChunkSizeBytes;
            }

            return (int)configured;
        }

        private long GetConfiguredLong(string key, long defaultValue)
        {
            long value;
            return long.TryParse(ConfigurationManager.AppSettings[key], out value) && value > 0
                ? value
                : defaultValue;
        }

        private string GetUploadFolderPath()
        {
            string folder = ConfigurationManager.AppSettings["UploadPath"] ?? "~\\Upload";
            if (folder.StartsWith("~", StringComparison.Ordinal))
            {
                folder = Server.MapPath(folder);
            }

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            return folder;
        }

        private string GetUploadSessionRootPath()
        {
            string path = Server.MapPath("~/App_Data/UploadSessions");
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        private string GetUploadSessionPath(string uploadId)
        {
            return Path.Combine(GetUploadSessionRootPath(), uploadId);
        }

        private string GetUploadStateFilePath(string uploadId)
        {
            return Path.Combine(GetUploadSessionPath(uploadId), UploadStateFileName);
        }

        private string GetUploadTempFilePath(string uploadId)
        {
            return Path.Combine(GetUploadSessionPath(uploadId), UploadTempFileName);
        }

        private string NormalizeUploadId(string uploadId)
        {
            if (string.IsNullOrWhiteSpace(uploadId))
            {
                return null;
            }

            string normalized = new string(uploadId
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());

            return normalized.Length == 0 ? null : normalized;
        }

        private UploadSessionState LoadUploadSessionState(string uploadId)
        {
            string stateFilePath = GetUploadStateFilePath(uploadId);
            if (!System.IO.File.Exists(stateFilePath))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<UploadSessionState>(System.IO.File.ReadAllText(stateFilePath));
        }

        private void SaveUploadSessionState(UploadSessionState state)
        {
            state.LastUpdatedUtc = DateTime.UtcNow;
            string sessionPath = GetUploadSessionPath(state.UploadId);
            if (!Directory.Exists(sessionPath))
            {
                Directory.CreateDirectory(sessionPath);
            }

            System.IO.File.WriteAllText(GetUploadStateFilePath(state.UploadId), JsonConvert.SerializeObject(state));
        }

        private long GetUploadedBytes(string uploadId, long fileSize)
        {
            string tempFilePath = GetUploadTempFilePath(uploadId);
            if (!System.IO.File.Exists(tempFilePath))
            {
                return 0;
            }

            long length = new FileInfo(tempFilePath).Length;
            return Math.Max(0, Math.Min(length, fileSize));
        }

        private void DeleteUploadSession(string uploadId)
        {
            string sessionPath = GetUploadSessionPath(uploadId);
            if (Directory.Exists(sessionPath))
            {
                Directory.Delete(sessionPath, true);
            }
        }

        private void CleanupExpiredUploadSessions()
        {
            string rootPath = GetUploadSessionRootPath();
            foreach (string sessionPath in Directory.GetDirectories(rootPath))
            {
                DateTime lastWriteUtc = Directory.GetLastWriteTimeUtc(sessionPath);
                if (DateTime.UtcNow - lastWriteUtc > UploadSessionRetention)
                {
                    Directory.Delete(sessionPath, true);
                }
            }
        }

        private bool TryResolveTargetFileName(string folder, string fileName, string mode, out string storedFileName, out string errorMessage)
        {
            storedFileName = Path.GetFileName(fileName);
            errorMessage = null;
            string fullPath = Path.Combine(folder, storedFileName);

            if (!System.IO.File.Exists(fullPath))
            {
                return true;
            }

            if (mode == "prohibit")
            {
                errorMessage = "❌ 檔案已存在";
                return false;
            }

            if (mode == "auto")
            {
                storedFileName = GetAutoNumberedFileName(folder, storedFileName);
            }

            return true;
        }

        private bool TryResolveCompletionFileName(string folder, UploadSessionState state, out string storedFileName, out string errorMessage)
        {
            storedFileName = state.StoredFileName;
            errorMessage = null;
            string fullPath = Path.Combine(folder, storedFileName);

            if (!System.IO.File.Exists(fullPath))
            {
                return true;
            }

            if (state.Mode == "overwrite")
            {
                return true;
            }

            if (state.Mode == "auto")
            {
                storedFileName = GetAutoNumberedFileName(folder, state.OriginalFileName);
                state.StoredFileName = storedFileName;
                SaveUploadSessionState(state);
                return true;
            }

            errorMessage = "❌ 檔案已存在";
            return false;
        }

        private string GetAutoNumberedFileName(string folder, string fileName)
        {
            string ext = Path.GetExtension(fileName);
            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string candidate = fileName;
            int version = 2;

            while (System.IO.File.Exists(Path.Combine(folder, candidate)))
            {
                candidate = $"{nameOnly}_v{version}{ext}";
                version++;
            }

            return candidate;
        }

        private JsonResult JsonError(int statusCode, string message)
        {
            SetResponseStatus(statusCode);
            return Json(new { message }, JsonRequestBehavior.DenyGet);
        }

        private void ClearUploadValidation()
        {
            Session.Remove(UploadPermissionSessionKey);
            Session.Remove(UploadPasswordSessionKey);
        }

        private void SetResponseStatus(int statusCode)
        {
            Response.StatusCode = statusCode;
            Response.TrySkipIisCustomErrors = true;
        }
    }
}