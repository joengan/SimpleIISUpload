# SimpleIISUpload - 簡易IIS上傳工具

## 📋 專案說明

此工具專為內網環境設計，用於臨時開放檔案上傳功能。當不需要開啟網路共享資料夾時，可透過IIS快速部署此工具進行檔案傳輸。

> ⚠️ **安全警告**：本工具僅限需要時臨時打開使用，平時應保持關閉狀態，以防範潛在安全風險。

---

## 🚀 快速開始

### 環境需求
- Windows Server 或 Windows 10/11 (含IIS功能)
- .NET Framework 4.7.2 或更高版本
- IIS 7.0 或更高版本

### 安裝步驟

#### 方法一：下載預編譯版本（推薦）

1. **下載發佈檔案**
   - 前往 [Releases 頁面](https://github.com/joengan/SimpleIISUpload/releases)
   - 下載最新版本的 `simpleiisupload-vX.X.X.zip` 或 `.tar.gz` 檔案
   - 例如解壓縮到目標目錄（例如：`C:\inetpub\wwwroot\SimpleIISUpload`）

2. **部署到IIS**
   - 開啟「IIS管理員」(inetmgr)
   - 新增網站或應用程式，指向解壓縮後的目錄
   - 確保應用程式集區使用 `.NET CLR 版本 v4.0`

3. **設定上傳路徑**（重要）
   - 開啟 `Web.config`
   - 修改 `UploadPath` 設定值：
     ```xml
     <add key="UploadPath" value="D:\UploadFiles" />
     ```
   - ⚠️ **請務必將上傳路徑設定在專案目錄之外**，避免安全風險

3. **設定上傳路徑**（重要）
   - 開啟 `Web.config`
   - 修改 `UploadPath` 設定值：
     ```xml
     <add key="UploadPath" value="D:\UploadFiles" />
     ```
   - ⚠️ **請務必將上傳路徑設定在專案目錄之外**，避免安全風險

4. **設定密碼權限**
   - 在 `Web.config` 中設定不同權限的密碼：
     ```xml
     <!-- 允許覆寫檔案的密碼，多組以「;」分隔 -->
     <add key="OverwritePasswords" value="admin123;manager456" />

     <!-- 禁止覆寫檔案的密碼，多組以「;」分隔 -->
     <add key="LimitedPasswords" value="user123;guest456" />

     <!-- 允許覆寫資料夾上傳的密碼，多組以「;」分隔 -->
     <add key="FolderOverwritePasswords" value="admin123" />

     <!-- 允許資料夾上傳但不可覆寫的密碼，多組以「;」分隔 -->
     <add key="FolderLimitedPasswords" value="user123" />
     ```

5. **設定IP白名單**（可選）
   - 在 `Web.config` 中設定允許存取的IP位址：
     ```xml
     <!-- 允許來源 IP，IPv4 / IPv6 以「;」分隔，未設定或留空則不限制 -->
     <add key="AllowedClientIps" value="192.168.1.100;192.168.1.101;::1;127.0.0.1" />
     ```
   - 若不需要IP限制，可設為空值：
     ```xml
     <add key="AllowedClientIps" value="" />
     ```

6. **確認目錄權限**
   - 確保IIS應用程式集區的身分識別（通常是 `IIS AppPool\應用程式集區名稱`）對 `UploadPath` 目錄具有「寫入」權限

#### 方法二：從原始碼建置（開發者）

1. **複製專案**
   ```powershell
   git clone https://github.com/joengan/SimpleIISUpload.git
   cd SimpleIISUpload
   ```

2. **使用 Visual Studio 發佈**
   - 開啟 `SimpleIISUpload.sln`
   - 右鍵點擊專案 → 發佈
   - 選擇 `FolderProfile` 或自訂發佈設定
   - 發佈至目標目錄

3. **後續步驟**
   - 依照「方法一」的步驟 2-6 完成部署設定

---

## 🛡️ 安全機制

### 1. 密碼保護
- 支援多組密碼，可設定不同權限等級
- **覆寫權限**：可以覆蓋已存在的檔案
- **限制權限**：禁止覆寫，已存在的檔案會被拒絕上傳
- 資料夾上傳亦有獨立的權限控制

### 2. IP白名單
- 支援IPv4與IPv6格式
- 多個IP以分號（`;`）分隔
- 未設定則不限制來源IP

### 3. 防暴力破解
- 密碼驗證失敗會有冷卻時間限制
- 多次失敗後會鎖定一段時間

### 4. 臨時關閉功能
將 `app_offline.htm.example` 重新命名為 `app_offline.htm`，IIS會自動將網站切換為離線狀態，顯示維護頁面。

**關閉步驟：**
```powershell
# 在專案根目錄執行
Rename-Item -Path "app_offline.htm.example" -NewName "app_offline.htm"
```

**重新啟用：**
```powershell
# 刪除或重新命名回 .example
Remove-Item -Path "app_offline.htm"
# 或
Rename-Item -Path "app_offline.htm" -NewName "app_offline.htm.example"
```

---

## ⚙️ 進階設定

### Web.config 參數說明

| 參數 | 說明 | 預設值 |
|------|------|--------|
| `UploadPath` | 檔案上傳的儲存路徑 | `~\Upload` |
| `MaxUploadFileBytes` | 單一檔案大小上限（位元組） | `8589934592` (8GB) |
| `UploadChunkSizeBytes` | 分塊上傳的區塊大小 | `8388608` (8MB) |
| `AllowedClientIps` | IP白名單，以「;」分隔 | `::1;127.0.0.1` |
| `OverwritePasswords` | 允許覆寫的密碼 | 空值 |
| `LimitedPasswords` | 禁止覆寫的密碼 | 空值 |
| `FolderOverwritePasswords` | 資料夾上傳覆寫權限密碼 | 空值 |
| `FolderLimitedPasswords` | 資料夾上傳限制權限密碼 | 空值 |

### IIS設定注意事項

- 確保應用程式集區的身分識別有權限寫入 `UploadPath` 目錄
- 若上傳大檔案，需調整IIS的請求限制：
  - `maxRequestLength`（Web.config）
  - `maxAllowedContentLength`（Web.config）
  - `executionTimeout`（Web.config）

---

## 📝 使用說明

1. **開啟網站**
   - 確保已設定好 `UploadPath`、密碼和IP限制
   - 瀏覽網站首頁

2. **選擇上傳模式**
   - **單檔上傳**：選擇單一檔案
   - **資料夾上傳**：選擇整個資料夾（保留目錄結構）

3. **設定覆寫模式**
   - **禁止覆寫**：若檔案已存在則拒絕上傳
   - **允許覆寫**：若檔案已存在則直接覆蓋
   - **自動編號**：若檔案已存在則自動重新命名

4. **輸入密碼並上傳**
   - 輸入對應權限的密碼
   - 點擊「開始上傳」按鈕

5. **完成後關閉網站**
   - 將 `app_offline.htm.example` 重新命名為 `app_offline.htm`

---

## ⚠️ 重要提醒

1. **此工具為內網臨時打開上傳檔案用**
   - 僅在需要時開啟，使用完畢後立即關閉
   - 不建議長期對外網開放

2. **不使用時請關閉服務**
   - 將 `app_offline.htm.example` 重新命名為 `app_offline.htm`
   - 或停止IIS網站

3. **除基本密碼鎖外，還可以設定IP鎖**
   - 建議搭配IP白名單使用，提高安全性
   - 限制僅特定IP可存取

4. **請記得調整UploadPath的路徑**
   - ⚠️ **不要將上傳路徑設定在專案目錄內**
   - 建議設定在獨立的磁碟分割或目錄
   - ⚠️ **重要安全提醒**：本工具未限制檔案類型，若上傳路徑在IIS可執行範圍內（如網站根目錄或虛擬目錄），上傳的 `.aspx`、`.ashx`、`.asp` 等檔案可能會被IIS直接執行，形成嚴重安全漏洞
   - 建議將 `UploadPath` 設定在IIS網站目錄之外，或透過IIS設定禁止該目錄執行腳本
   - 本工具使用IIS部署，具備一定技術門檻，相信使用者具備基本資安意識，請自行評估風險並採取適當防護措施

5. **僅限需要時臨時打開，平時不要打開，慎防危險**
   - 避免成為攻擊目標
   - 定期檢查上傳的檔案內容

---

## 📄 授權

本專案採用 MIT 授權條款 - 詳見 [LICENSE](LICENSE) 檔案

---

## 🔗 相關連結

- GitHub Repository: https://github.com/joengan/SimpleIISUpload
