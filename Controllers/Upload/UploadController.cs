using System;
using System.Collections.Generic;
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
    [AllowedClientIpRestriction]
    public class UploadController : Controller
    {
        private const string UploadPermissionSessionKey = "UploadPermission";
        private const string UploadPasswordSessionKey = "UploadPassword";
        private const string UploadKindSessionKey = "UploadKind";
        private const string UploadStateFileSuffix = ".upload.json";
        private const string UploadTempFileSuffix = ".upload.tmp";
        private const long DefaultMaxUploadFileBytes = 8L * 1024 * 1024 * 1024;
        private const int DefaultChunkSizeBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan UploadSessionRetention = TimeSpan.FromHours(1);

        private enum UploadPermission
        {
            None,
            Limited,
            Overwrite
        }

        private enum UploadSelectionKind
        {
            File,
            Folder
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
            public string UploadKind { get; set; }
            public string OriginalFileName { get; set; }
            public string RelativePath { get; set; }
            public string StoredRelativePath { get; set; }
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
        public ActionResult ValidatePermission(string pwd, string mode, string uploadKind)
        {
            UploadSelectionKind selectionKind;
            if (!TryParseUploadKind(uploadKind, out selectionKind))
            {
                ClearUploadValidation();
                SetResponseStatus((int)HttpStatusCode.BadRequest);
                return Content("❌ 上傳類型錯誤");
            }

            PasswordValidationResult validation = ValidatePasswordAndMode(pwd, mode, selectionKind);
            if (!validation.IsSuccess)
            {
                ClearUploadValidation();
                SetResponseStatus(validation.StatusCode);
                return Content(validation.Message);
            }

            Session[UploadPermissionSessionKey] = validation.Permission.ToString();
            Session[UploadPasswordSessionKey] = pwd;
            Session[UploadKindSessionKey] = GetUploadKindValue(selectionKind);
            return Content("OK");
        }

        [HttpPost]
        public ActionResult InitializeUpload(string uploadId, string fileName, long fileSize, string relativePath, string pwd, string mode, string uploadKind)
        {
            UploadSelectionKind selectionKind;
            if (!TryParseUploadKind(uploadKind, out selectionKind))
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳類型錯誤");
            }

            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode, selectionKind))
            {
                ClearUploadValidation();
                UploadRequestThrottleResult throttle = UploadRequestThrottle.BlockAbnormalUpload(Request, "UploadInitializeWithoutValidatedSession");
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

            string normalizedRelativePath;
            string errorMessage;
            if (!TryNormalizeRelativePath(relativePath, safeFileName, out normalizedRelativePath, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.BadRequest, errorMessage);
            }

            if (fileSize < 0)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 檔案大小錯誤");
            }

            long maxUploadFileBytes = GetConfiguredMaxUploadFileBytes();
            if (fileSize > maxUploadFileBytes)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 檔案超過 8GB 上限");
            }

            CleanupExpiredUploadSessions();

            string normalizedUploadKind = GetUploadKindValue(selectionKind);
            UploadSessionState state = LoadUploadSessionState(normalizedUploadId);
            if (state != null)
            {
                string stateUploadKind = GetStateUploadKind(state);
                string stateRelativePath = GetStateRelativePath(state);
                if (!string.Equals(state.OriginalFileName, safeFileName, StringComparison.Ordinal)
                    || state.FileSize != fileSize
                    || !string.Equals(state.Mode, mode, StringComparison.Ordinal)
                    || !string.Equals(stateUploadKind, normalizedUploadKind, StringComparison.Ordinal)
                    || !string.Equals(stateRelativePath, normalizedRelativePath, StringComparison.Ordinal))
                {
                    DeleteUploadSession(normalizedUploadId);
                    state = null;
                }
                else
                {
                    state.UploadedBytes = GetUploadedBytes(state.UploadId, state.FileSize);
                    state.UploadKind = normalizedUploadKind;
                    state.RelativePath = normalizedRelativePath;
                    state.StoredRelativePath = GetStateStoredRelativePath(state);
                    state.StoredFileName = Path.GetFileName(state.StoredRelativePath);
                    SaveUploadSessionState(state);
                    return Json(new
                    {
                        uploadId = state.UploadId,
                        fileName = state.StoredFileName,
                        relativePath = state.StoredRelativePath,
                        uploadedBytes = state.UploadedBytes,
                        chunkSize = state.ChunkSize,
                        maxFileSize = maxUploadFileBytes,
                        isResuming = state.UploadedBytes > 0
                    });
                }
            }

            string storedRelativePath;
            if (!TryResolveTargetRelativePath(GetUploadFolderPath(), normalizedRelativePath, mode, out storedRelativePath, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.Conflict, errorMessage);
            }

            state = new UploadSessionState
            {
                UploadId = normalizedUploadId,
                UploadKind = normalizedUploadKind,
                OriginalFileName = safeFileName,
                RelativePath = normalizedRelativePath,
                StoredRelativePath = storedRelativePath,
                StoredFileName = Path.GetFileName(storedRelativePath),
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
                relativePath = state.StoredRelativePath,
                uploadedBytes = state.UploadedBytes,
                chunkSize = state.ChunkSize,
                maxFileSize = maxUploadFileBytes,
                isResuming = false
            });
        }

        [HttpPost]
        public ActionResult UploadChunk(string uploadId, int chunkIndex, int totalChunks, long chunkStart, string pwd, string mode, string uploadKind, HttpPostedFileBase chunk)
        {
            UploadRequestThrottleResult throttle = UploadRequestThrottle.GetThrottleResult(Request);
            if (throttle.IsThrottled)
            {
                ClearUploadValidation();
                return JsonError(throttle.StatusCode, throttle.Message);
            }

            UploadSelectionKind selectionKind;
            if (!TryParseUploadKind(uploadKind, out selectionKind))
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳類型錯誤");
            }

            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode, selectionKind))
            {
                ClearUploadValidation();
                throttle = UploadRequestThrottle.BlockAbnormalUpload(Request, "UploadChunkWithoutValidatedSession");
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

            if (!string.Equals(state.Mode, mode, StringComparison.Ordinal)
                || !string.Equals(GetStateUploadKind(state), GetUploadKindValue(selectionKind), StringComparison.Ordinal))
            {
                return JsonError((int)HttpStatusCode.Conflict, "❌ 上傳資訊與續傳資訊不一致");
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
        public ActionResult CompleteUpload(string uploadId, string pwd, string mode, string uploadKind)
        {
            UploadSelectionKind selectionKind;
            if (!TryParseUploadKind(uploadKind, out selectionKind))
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳類型錯誤");
            }

            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode, selectionKind))
            {
                ClearUploadValidation();
                UploadRequestThrottleResult throttle = UploadRequestThrottle.BlockAbnormalUpload(Request, "CompleteUploadWithoutValidatedSession");
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

            if (!string.Equals(state.Mode, mode, StringComparison.Ordinal)
                || !string.Equals(GetStateUploadKind(state), GetUploadKindValue(selectionKind), StringComparison.Ordinal))
            {
                return JsonError((int)HttpStatusCode.Conflict, "❌ 上傳資訊與續傳資訊不一致");
            }

            string tempFilePath = GetUploadTempFilePath(state.UploadId);
            if (state.FileSize > 0 && !System.IO.File.Exists(tempFilePath))
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

            string finalRelativePath;
            string errorMessage;
            if (!TryResolveCompletionRelativePath(GetUploadFolderPath(), state, out finalRelativePath, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.Conflict, errorMessage);
            }

            string finalPath;
            if (!TryGetSafeAbsolutePath(GetUploadFolderPath(), finalRelativePath, out finalPath))
            {
                DeleteUploadSession(state.UploadId);
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳路徑錯誤");
            }

            string targetDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            bool existedBeforeCompletion = System.IO.File.Exists(finalPath);
            if (string.Equals(state.Mode, "overwrite", StringComparison.Ordinal) && existedBeforeCompletion)
            {
                System.IO.File.Delete(finalPath);
            }

            if (state.FileSize == 0)
            {
                using (System.IO.FileStream stream = new System.IO.FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                }
            }
            else
            {
                System.IO.File.Move(tempFilePath, finalPath);
            }

            string originalRelativePath = GetStateRelativePath(state).Replace('\\', '/');
            string completedRelativePath = finalRelativePath.Replace('\\', '/');
            string action = "created";
            if (string.Equals(state.Mode, "overwrite", StringComparison.Ordinal))
            {
                action = existedBeforeCompletion ? "overwritten" : "created";
            }
            else if (string.Equals(state.Mode, "auto", StringComparison.Ordinal)
                && !string.Equals(completedRelativePath, originalRelativePath, StringComparison.Ordinal))
            {
                action = "renamed";
            }

            DeleteUploadSession(state.UploadId);
            return Json(new
            {
                message = $"✅ 成功上傳: {completedRelativePath}",
                action,
                relativePath = completedRelativePath,
                originalRelativePath
            });
        }

        [HttpPost]
        public ActionResult CreateFolder(string relativePath, string pwd, string mode, string uploadKind)
        {
            UploadSelectionKind selectionKind;
            if (!TryParseUploadKind(uploadKind, out selectionKind) || selectionKind != UploadSelectionKind.Folder)
            {
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳類型錯誤");
            }

            if (!IsSupportedMode(mode))
            {
                ClearUploadValidation();
                return JsonError((int)HttpStatusCode.BadRequest, "❌ 上傳模式錯誤");
            }

            if (!HasValidatedUploadSession(pwd, mode, selectionKind))
            {
                ClearUploadValidation();
                UploadRequestThrottleResult throttle = UploadRequestThrottle.BlockAbnormalUpload(Request, "CreateFolderWithoutValidatedSession");
                return JsonError(throttle.StatusCode, throttle.Message);
            }

            string normalizedRelativePath;
            string errorMessage;
            if (!TryNormalizeDirectoryRelativePath(relativePath, out normalizedRelativePath, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.BadRequest, errorMessage);
            }

            string fullPath;
            if (!TryResolveTargetDirectoryPath(GetUploadFolderPath(), normalizedRelativePath, out fullPath, out errorMessage))
            {
                return JsonError((int)HttpStatusCode.Conflict, errorMessage);
            }

            bool directoryExists = Directory.Exists(fullPath);
            Directory.CreateDirectory(fullPath);
            return Json(new
            {
                message = "OK",
                action = directoryExists ? "folderExisting" : "folderCreated",
                relativePath = normalizedRelativePath.Replace('\\', '/')
            });
        }

        [HttpPost]
        public ActionResult ClearValidatedUploadSession()
        {
            ClearUploadValidation();
            return new HttpStatusCodeResult((int)HttpStatusCode.NoContent);
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

            if (!HasValidatedUploadSession(pwd, mode, UploadSelectionKind.File))
            {
                ClearUploadValidation();
                throttle = UploadRequestThrottle.BlockAbnormalUpload(Request, "DirectUploadWithoutValidatedSession");
                SetResponseStatus(throttle.StatusCode);
                return Content(throttle.Message);
            }

            string folder = GetUploadFolderPath();
            string fileName = Path.GetFileName(file.FileName);
            string fullPath = Path.Combine(folder, fileName);

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
                    fileName = Path.GetFileName(GetAutoNumberedRelativePath(folder, fileName));
                    fullPath = Path.Combine(folder, fileName);
                }
            }

            file.SaveAs(fullPath);
            ClearUploadValidation();
            return Content($"✅ 成功上傳: {fileName}");
        }

        private PasswordValidationResult ValidatePasswordAndMode(string pwd, string mode, UploadSelectionKind uploadKind)
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

            string[] overwritePasswords = uploadKind == UploadSelectionKind.Folder
                ? GetPasswords("FolderOverwritePasswords")
                : GetPasswords("OverwritePasswords").Concat(GetPasswords("FolderOverwritePasswords")).Distinct().ToArray();
            string[] limitedPasswords = uploadKind == UploadSelectionKind.Folder
                ? GetPasswords("FolderLimitedPasswords")
                : GetPasswords("LimitedPasswords").Concat(GetPasswords("FolderLimitedPasswords")).Distinct().ToArray();

            if (uploadKind == UploadSelectionKind.Folder && overwritePasswords.Length == 0 && limitedPasswords.Length == 0)
            {
                return new PasswordValidationResult
                {
                    IsSuccess = false,
                    StatusCode = (int)HttpStatusCode.Forbidden,
                    Message = "❌ 目前未開放資料夾上傳",
                    Permission = UploadPermission.None
                };
            }

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

            if (uploadKind == UploadSelectionKind.Folder && IsKnownFileUploadPassword(pwd))
            {
                return new PasswordValidationResult
                {
                    IsSuccess = false,
                    StatusCode = (int)HttpStatusCode.Forbidden,
                    Message = "❌ 此密碼未開放資料夾上傳",
                    Permission = UploadPermission.None
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

        private bool TryParseUploadKind(string uploadKind, out UploadSelectionKind selectionKind)
        {
            if (string.IsNullOrWhiteSpace(uploadKind) || string.Equals(uploadKind, "file", StringComparison.OrdinalIgnoreCase))
            {
                selectionKind = UploadSelectionKind.File;
                return true;
            }

            if (string.Equals(uploadKind, "folder", StringComparison.OrdinalIgnoreCase))
            {
                selectionKind = UploadSelectionKind.Folder;
                return true;
            }

            selectionKind = UploadSelectionKind.File;
            return false;
        }

        private string GetUploadKindValue(UploadSelectionKind selectionKind)
        {
            return selectionKind == UploadSelectionKind.Folder ? "folder" : "file";
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

        private bool IsKnownFileUploadPassword(string pwd)
        {
            return GetPasswords("OverwritePasswords").Contains(pwd)
                || GetPasswords("LimitedPasswords").Contains(pwd);
        }

        private bool HasValidatedUploadSession(string pwd, string mode, UploadSelectionKind uploadKind)
        {
            string sessionPassword = Session[UploadPasswordSessionKey] as string;
            string sessionPermission = Session[UploadPermissionSessionKey] as string;
            string sessionUploadKind = Session[UploadKindSessionKey] as string;
            UploadPermission permission;

            if (!string.Equals(sessionPassword, pwd, StringComparison.Ordinal)
                || !Enum.TryParse(sessionPermission, out permission)
                || permission == UploadPermission.None
                || !string.Equals(sessionUploadKind, GetUploadKindValue(uploadKind), StringComparison.Ordinal))
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

        private string GetUploadStateFilePath(string uploadId)
        {
            return Path.Combine(GetUploadFolderPath(), uploadId + UploadStateFileSuffix);
        }

        private string GetUploadTempFilePath(string uploadId)
        {
            return Path.Combine(GetUploadFolderPath(), uploadId + UploadTempFileSuffix);
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

            try
            {
                return JsonConvert.DeserializeObject<UploadSessionState>(System.IO.File.ReadAllText(stateFilePath));
            }
            catch
            {
                DeleteUploadSession(uploadId);
                return null;
            }
        }

        private void SaveUploadSessionState(UploadSessionState state)
        {
            state.LastUpdatedUtc = DateTime.UtcNow;
            System.IO.File.WriteAllText(GetUploadStateFilePath(state.UploadId), JsonConvert.SerializeObject(state));
        }

        private long GetUploadedBytes(string uploadId, long fileSize)
        {
            if (fileSize <= 0)
            {
                return 0;
            }

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
            string stateFilePath = GetUploadStateFilePath(uploadId);
            if (System.IO.File.Exists(stateFilePath))
            {
                System.IO.File.Delete(stateFilePath);
            }

            string tempFilePath = GetUploadTempFilePath(uploadId);
            if (System.IO.File.Exists(tempFilePath))
            {
                System.IO.File.Delete(tempFilePath);
            }
        }

        private void CleanupExpiredUploadSessions()
        {
            string folder = GetUploadFolderPath();
            foreach (string stateFilePath in Directory.GetFiles(folder, "*" + UploadStateFileSuffix))
            {
                string uploadId = GetUploadIdFromStateFilePath(stateFilePath);
                if (string.IsNullOrWhiteSpace(uploadId))
                {
                    continue;
                }

                DateTime lastWriteUtc = System.IO.File.GetLastWriteTimeUtc(stateFilePath);
                if (DateTime.UtcNow - lastWriteUtc > UploadSessionRetention)
                {
                    DeleteUploadSession(uploadId);
                }
            }

            foreach (string tempFilePath in Directory.GetFiles(folder, "*" + UploadTempFileSuffix))
            {
                string uploadId = GetUploadIdFromTempFilePath(tempFilePath);
                if (string.IsNullOrWhiteSpace(uploadId))
                {
                    continue;
                }

                string stateFilePath = GetUploadStateFilePath(uploadId);
                DateTime lastWriteUtc = System.IO.File.GetLastWriteTimeUtc(tempFilePath);
                if (!System.IO.File.Exists(stateFilePath) || DateTime.UtcNow - lastWriteUtc > UploadSessionRetention)
                {
                    DeleteUploadSession(uploadId);
                }
            }
        }

        private string GetUploadIdFromStateFilePath(string stateFilePath)
        {
            string fileName = Path.GetFileName(stateFilePath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(UploadStateFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileName.Substring(0, fileName.Length - UploadStateFileSuffix.Length);
        }

        private string GetUploadIdFromTempFilePath(string tempFilePath)
        {
            string fileName = Path.GetFileName(tempFilePath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(UploadTempFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileName.Substring(0, fileName.Length - UploadTempFileSuffix.Length);
        }

        private bool TryNormalizeRelativePath(string relativePath, string safeFileName, out string normalizedRelativePath, out string errorMessage)
        {
            normalizedRelativePath = null;
            errorMessage = null;

            string candidate = string.IsNullOrWhiteSpace(relativePath) ? safeFileName : relativePath;
            candidate = candidate.Replace('/', '\\').Trim();
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("\\", StringComparison.Ordinal) || Path.IsPathRooted(candidate))
            {
                errorMessage = "❌ 上傳路徑錯誤";
                return false;
            }

            string[] segments = candidate
                .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => (segment ?? string.Empty).Trim())
                .ToArray();

            if (segments.Length == 0)
            {
                errorMessage = "❌ 上傳路徑錯誤";
                return false;
            }

            foreach (string segment in segments)
            {
                if (segment.Length == 0
                    || segment == "."
                    || segment == ".."
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || segment != segment.TrimEnd(' ', '.'))
                {
                    errorMessage = "❌ 上傳路徑錯誤";
                    return false;
                }
            }

            if (!string.Equals(segments[segments.Length - 1], safeFileName, StringComparison.Ordinal))
            {
                errorMessage = "❌ 檔名與路徑不一致";
                return false;
            }

            normalizedRelativePath = string.Join("\\", segments);
            return true;
        }

        private bool TryNormalizeDirectoryRelativePath(string relativePath, out string normalizedRelativePath, out string errorMessage)
        {
            normalizedRelativePath = null;
            errorMessage = null;

            string candidate = (relativePath ?? string.Empty).Replace('/', '\\').Trim();
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("\\", StringComparison.Ordinal) || Path.IsPathRooted(candidate))
            {
                errorMessage = "❌ 資料夾路徑錯誤";
                return false;
            }

            string[] segments = candidate
                .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => (segment ?? string.Empty).Trim())
                .ToArray();

            if (segments.Length == 0)
            {
                errorMessage = "❌ 資料夾路徑錯誤";
                return false;
            }

            foreach (string segment in segments)
            {
                if (segment.Length == 0
                    || segment == "."
                    || segment == ".."
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || segment != segment.TrimEnd(' ', '.'))
                {
                    errorMessage = "❌ 資料夾路徑錯誤";
                    return false;
                }
            }

            normalizedRelativePath = string.Join("\\", segments);
            return true;
        }

        private bool TryResolveTargetRelativePath(string folder, string relativePath, string mode, out string storedRelativePath, out string errorMessage)
        {
            storedRelativePath = relativePath;
            errorMessage = null;

            string fullPath;
            if (!TryGetSafeAbsolutePath(folder, storedRelativePath, out fullPath))
            {
                errorMessage = "❌ 上傳路徑錯誤";
                return false;
            }

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
                storedRelativePath = GetAutoNumberedRelativePath(folder, relativePath);
            }

            return true;
        }

        private bool TryResolveTargetDirectoryPath(string folder, string relativePath, out string fullPath, out string errorMessage)
        {
            errorMessage = null;
            if (!TryGetSafeAbsolutePath(folder, relativePath, out fullPath))
            {
                errorMessage = "❌ 資料夾路徑錯誤";
                return false;
            }

            if (System.IO.File.Exists(fullPath))
            {
                errorMessage = "❌ 已存在同名檔案";
                return false;
            }

            return true;
        }

        private bool TryResolveCompletionRelativePath(string folder, UploadSessionState state, out string storedRelativePath, out string errorMessage)
        {
            storedRelativePath = GetStateStoredRelativePath(state);
            errorMessage = null;

            string fullPath;
            if (!TryGetSafeAbsolutePath(folder, storedRelativePath, out fullPath))
            {
                errorMessage = "❌ 上傳路徑錯誤";
                return false;
            }

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
                storedRelativePath = GetAutoNumberedRelativePath(folder, GetStateRelativePath(state));
                state.StoredRelativePath = storedRelativePath;
                state.StoredFileName = Path.GetFileName(storedRelativePath);
                SaveUploadSessionState(state);
                return true;
            }

            errorMessage = "❌ 檔案已存在";
            return false;
        }

        private string GetAutoNumberedRelativePath(string folder, string relativePath)
        {
            string directoryPath = Path.GetDirectoryName(relativePath) ?? string.Empty;
            string fileName = Path.GetFileName(relativePath);
            string ext = Path.GetExtension(fileName);
            string nameOnly = Path.GetFileNameWithoutExtension(fileName);
            string candidate = fileName;
            int version = 2;
            string candidateRelativePath = CombineRelativePath(directoryPath, candidate);
            string candidateFullPath;

            while (TryGetSafeAbsolutePath(folder, candidateRelativePath, out candidateFullPath) && System.IO.File.Exists(candidateFullPath))
            {
                candidate = $"{nameOnly}_v{version}{ext}";
                candidateRelativePath = CombineRelativePath(directoryPath, candidate);
                version++;
            }

            return candidateRelativePath;
        }

        private string CombineRelativePath(string directoryPath, string fileName)
        {
            return string.IsNullOrWhiteSpace(directoryPath)
                ? fileName
                : Path.Combine(directoryPath, fileName);
        }

        private bool TryGetSafeAbsolutePath(string rootFolder, string relativePath, out string fullPath)
        {
            fullPath = null;

            try
            {
                string rootFullPath = Path.GetFullPath(rootFolder);
                string candidateFullPath = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
                string normalizedRoot = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (!candidateFullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                fullPath = candidateFullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private string GetStateUploadKind(UploadSessionState state)
        {
            return string.IsNullOrWhiteSpace(state == null ? null : state.UploadKind)
                ? GetUploadKindValue(UploadSelectionKind.File)
                : state.UploadKind;
        }

        private string GetStateRelativePath(UploadSessionState state)
        {
            if (!string.IsNullOrWhiteSpace(state == null ? null : state.RelativePath))
            {
                return state.RelativePath;
            }

            return state == null ? null : state.OriginalFileName;
        }

        private string GetStateStoredRelativePath(UploadSessionState state)
        {
            if (!string.IsNullOrWhiteSpace(state == null ? null : state.StoredRelativePath))
            {
                return state.StoredRelativePath;
            }

            if (!string.IsNullOrWhiteSpace(state == null ? null : state.StoredFileName))
            {
                return CombineRelativePath(Path.GetDirectoryName(GetStateRelativePath(state)) ?? string.Empty, state.StoredFileName);
            }

            return GetStateRelativePath(state);
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
            Session.Remove(UploadKindSessionKey);
        }

        private void SetResponseStatus(int statusCode)
        {
            Response.StatusCode = statusCode;
            Response.TrySkipIisCustomErrors = true;
        }
    }
}
