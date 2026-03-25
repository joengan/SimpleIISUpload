type ConflictMode = "prohibit" | "overwrite" | "auto";
type UploadKind = "file" | "folder";
type UploadSession = {
    uploadId: string;
    fileName: string;
    relativePath: string;
    uploadedBytes: number;
    chunkSize: number;
    maxFileSize: number;
    isResuming: boolean;
};
type ChunkUploadResult = { uploadedBytes: number; message: string; isCompleted: boolean; };
type CompleteUploadResult = { message: string; action: string; relativePath: string; originalRelativePath: string; };
type CreateFolderResult = { message: string; action: string; relativePath: string; };
type ErrorResponse = { message?: string; };
type RequestError = Error & { statusCode?: number; };
type FolderUploadResult = {
    itemType: 'file' | 'emptyFolder';
    relativePath: string;
    tone: 'success' | 'warning' | 'error';
    summary: string;
};
type SelectedUploadItem = {
    itemType: 'file' | 'emptyFolder';
    file: File | null;
    relativePath: string;
    displayPath: string;
    uploadId: string;
    uploadedBytes: number;
    progressBytes: number;
    isCompleted: boolean;
};
type UploadSelection = {
    kind: UploadKind;
    items: SelectedUploadItem[];
    label: string;
    tooltip: string;
};

type DataTransferItemWithEntry = DataTransferItem & {
    webkitGetAsEntry?: () => FileSystemEntry | null;
};

type DirectoryPickerWindow = Window & {
    showDirectoryPicker?: () => Promise<any>;
};

new class {
    private selection: UploadSelection | null = null;
    private isUploading = false;
    private isLockedOut = false;
    private lockoutTimerId: number | null = null;
    private lockoutUntil = 0;
    private fileDragDeactivateTimerId: number | null = null;
    private shouldOfferResume = false;
    private readonly validatePermissionLockoutMs = 60 * 1000;
    private readonly chunkRetryCount = 3;
    private readonly chunkRetryDelayMs = 1000;
    private uploadCard = document.getElementById('uploadCard') as HTMLDivElement;
    private validateUrl = (this.uploadCard.dataset.validateUrl || "ValidatePermission");
    private initializeUrl = (this.uploadCard.dataset.initUrl || "InitializeUpload");
    private uploadChunkUrl = (this.uploadCard.dataset.uploadChunkUrl || "UploadChunk");
    private completeUrl = (this.uploadCard.dataset.completeUrl || "CompleteUpload");
    private createFolderUrl = (this.uploadCard.dataset.createFolderUrl || "CreateFolder");
    private clearSessionUrl = (this.uploadCard.dataset.clearSessionUrl || "ClearValidatedUploadSession");
    private dropZone = document.getElementById('dropZone') as HTMLDivElement;
    private fileInput = document.getElementById('fileInput') as HTMLInputElement;
    private pickFilesBtn = document.getElementById('pickFilesBtn') as HTMLButtonElement;
    private pickFolderBtn = document.getElementById('pickFolderBtn') as HTMLButtonElement;
    private filePathBox = document.getElementById('filePath') as HTMLSpanElement;
    private progressBox = document.getElementById('uploadProgress') as HTMLDivElement;
    private progressBar = document.getElementById('uploadProgressBar') as HTMLDivElement;
    private progressText = document.getElementById('uploadProgressText') as HTMLSpanElement;
    private uploadResultsBox = document.getElementById('uploadResults') as HTMLDivElement;
    private uploadResultsSummary = document.getElementById('uploadResultsSummary') as HTMLSpanElement;
    private uploadResultsList = document.getElementById('uploadResultsList') as HTMLUListElement;
    private statusBox = document.getElementById('status') as HTMLParagraphElement;
    private pwdInput = document.getElementById('pwd') as HTMLInputElement;
    private togglePwdBtn = document.getElementById('togglePwdBtn') as HTMLButtonElement;
    private togglePwdText = document.getElementById('togglePwdText') as HTMLSpanElement;
    private modeInputs = Array.from(document.querySelectorAll('input[name="mode"]')) as HTMLInputElement[];
    private folderThreadSettings = document.getElementById('folderThreadSettings') as HTMLDetailsElement;
    private folderThreadInput = document.getElementById('folderThreadCount') as HTMLInputElement;
    private folderThreadHint = document.getElementById('folderThreadHint') as HTMLParagraphElement;
    private btn = document.getElementById('uploadBtn') as HTMLButtonElement;
    private readonly initialFilePathText = this.filePathBox.innerText;
    private readonly initialStatusText = this.statusBox.innerText;
    private readonly initialModeValue = ((this.modeInputs.find(input => input.checked) || this.modeInputs[0]).value as ConflictMode);
    private readonly emptyFileButtonText = this.btn.innerText;
    private readonly readyButtonText = "確認上傳";
    private readonly resumeButtonText = "繼續上傳";
    private allowedMaxUploadThreads = 16;
    private defaultMaxUploadThreads = 8;
    private pickerKind: UploadKind = 'file';
    private isPasswordVisible = false;

    constructor() {
        this.initializeFolderThreadSettings();
        this.initEvents();
        this.updatePasswordVisibility();
        this.refreshSubmitButton();
    }

    private initEvents() {
        this.pickFilesBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (!this.canSelectFile()) {
                return;
            }

            void this.openPicker('file');
        });

        this.pickFolderBtn.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (!this.canSelectFile()) {
                return;
            }

            void this.openPicker('folder');
        });

        this.fileInput.onchange = (e) => this.handlePickerSelect(e);
        this.togglePwdBtn.addEventListener('mousedown', (e) => e.preventDefault());
        this.togglePwdBtn.addEventListener('click', () => this.togglePasswordVisibility());
        this.folderThreadInput.addEventListener('change', () => this.normalizeFolderThreadInput());
        this.folderThreadInput.addEventListener('blur', () => this.normalizeFolderThreadInput());
        this.pwdInput.addEventListener('keydown', (e) => {
            if (e.key !== 'Enter' || this.btn.disabled) {
                return;
            }

            e.preventDefault();
            this.btn.click();
        });

        ['dragenter', 'dragover'].forEach(name => {
            document.addEventListener(name, (e) => {
                const dragEvent = e as DragEvent;
                if (!this.hasDraggedFiles(dragEvent.dataTransfer)) {
                    return;
                }

                dragEvent.preventDefault();
                this.cancelFileDragDeactivation();

                if (dragEvent.dataTransfer) {
                    dragEvent.dataTransfer.dropEffect = 'copy';
                }

                if (!this.isUploading) {
                    this.setFileDragActive(true);
                }
            });
        });

        document.addEventListener('dragleave', (e) => {
            const dragEvent = e as DragEvent;
            if (!this.hasDraggedFiles(dragEvent.dataTransfer)) {
                return;
            }

            dragEvent.preventDefault();
            this.scheduleFileDragDeactivation();
        });

        document.addEventListener('drop', (e) => {
            const dragEvent = e as DragEvent;
            if (!this.hasDraggedFiles(dragEvent.dataTransfer)) {
                return;
            }

            dragEvent.preventDefault();
            this.clearFileDragState();
            if (!this.canSelectFile() || !dragEvent.dataTransfer) {
                return;
            }

            void this.handleDrop(dragEvent.dataTransfer);
        });

        window.addEventListener('blur', () => this.clearFileDragState());

        this.btn.onclick = () => {
            void this.upload();
        };
    }

    private canSubmit() {
        return !!this.selection && this.selection.items.length > 0 && !this.isUploading && !this.isLockedOut;
    }

    private canSelectFile() {
        return !this.isUploading;
    }

    private setUploadingState(isUploading: boolean) {
        this.isUploading = isUploading;
        this.clearFileDragState();
        this.refreshSubmitButton();
        this.fileInput.disabled = isUploading;
        this.pickFilesBtn.disabled = isUploading;
        this.pickFolderBtn.disabled = isUploading;
        this.pwdInput.disabled = isUploading;
        this.togglePwdBtn.disabled = isUploading;
        this.modeInputs.forEach(input => input.disabled = isUploading);
        this.folderThreadInput.disabled = isUploading;
        this.folderThreadSettings.classList.toggle('is-disabled', isUploading);
        this.folderThreadSettings.setAttribute('aria-disabled', `${isUploading}`);
        this.dropZone.classList.toggle('is-disabled', isUploading);
        this.dropZone.setAttribute('aria-disabled', `${isUploading}`);
    }

    private initializeFolderThreadSettings() {
        this.allowedMaxUploadThreads = Math.max(1, this.parsePositiveInteger(this.uploadCard.dataset.allowedMaxUploadThreads, 16));
        this.defaultMaxUploadThreads = Math.min(
            this.allowedMaxUploadThreads,
            Math.max(1, this.parsePositiveInteger(this.uploadCard.dataset.defaultMaxUploadThreads, 8)));

        this.folderThreadInput.step = '1';
        this.folderThreadInput.min = '1';
        this.folderThreadInput.max = `${this.allowedMaxUploadThreads}`;
        this.folderThreadInput.value = `${this.defaultMaxUploadThreads}`;
        this.folderThreadHint.innerText = `可設定 1 ~ ${this.allowedMaxUploadThreads}，預設 ${this.defaultMaxUploadThreads}。`;
    }

    private parsePositiveInteger(value: string | undefined, fallbackValue: number) {
        const parsedValue = Number(value);
        return Number.isFinite(parsedValue) && parsedValue > 0
            ? Math.round(parsedValue)
            : fallbackValue;
    }

    private normalizeFolderThreadInput() {
        this.folderThreadInput.value = `${this.getFolderParallelThreads()}`;
    }

    private getFolderParallelThreads() {
        const rawValue = Number(this.folderThreadInput.value);
        const normalizedValue = Number.isFinite(rawValue) ? Math.round(rawValue) : this.defaultMaxUploadThreads;
        return Math.min(this.allowedMaxUploadThreads, Math.max(1, normalizedValue));
    }

    private togglePasswordVisibility() {
        this.isPasswordVisible = !this.isPasswordVisible;
        this.updatePasswordVisibility();
        this.pwdInput.focus();
    }

    private updatePasswordVisibility() {
        const labelText = this.isPasswordVisible ? "隱藏密碼" : "顯示密碼";

        this.pwdInput.type = this.isPasswordVisible ? 'text' : 'password';
        this.togglePwdBtn.classList.toggle('is-visible', this.isPasswordVisible);
        this.togglePwdBtn.title = labelText;
        this.togglePwdBtn.setAttribute('aria-label', labelText);
        this.togglePwdBtn.setAttribute('aria-pressed', `${this.isPasswordVisible}`);
        this.togglePwdText.innerText = labelText;
    }

    private refreshSubmitButton() {
        this.btn.disabled = !this.canSubmit();

        if (this.isUploading) {
            this.btn.innerText = "處理中...";
            return;
        }

        if (this.isLockedOut) {
            const remainingSeconds = Math.max(1, Math.ceil((this.lockoutUntil - Date.now()) / 1000));
            this.btn.innerText = `請稍後 ${remainingSeconds} 秒再試`;
            return;
        }

        if (!this.selection || this.selection.items.length === 0) {
            this.btn.innerText = this.emptyFileButtonText;
            return;
        }

        this.btn.innerText = this.shouldOfferResume ? this.resumeButtonText : this.readyButtonText;
    }

    private clearLockout() {
        this.isLockedOut = false;
        this.lockoutUntil = 0;

        if (this.lockoutTimerId !== null) {
            window.clearInterval(this.lockoutTimerId);
            this.lockoutTimerId = null;
        }

        this.refreshSubmitButton();
    }

    private startLockout(durationMs: number) {
        this.isLockedOut = true;
        this.lockoutUntil = Date.now() + durationMs;

        if (this.lockoutTimerId !== null) {
            window.clearInterval(this.lockoutTimerId);
        }

        this.refreshSubmitButton();

        this.lockoutTimerId = window.setInterval(() => {
            if (Date.now() >= this.lockoutUntil) {
                this.clearLockout();
                return;
            }

            this.refreshSubmitButton();
        }, 1000);
    }

    private hasDraggedFiles(dataTransfer?: DataTransfer | null) {
        return !!dataTransfer && Array.from(dataTransfer.types).indexOf('Files') >= 0;
    }

    private setFileDragActive(isActive: boolean) {
        this.uploadCard.classList.toggle('is-file-dragging', isActive);
        this.dropZone.classList.toggle('active', isActive);
        document.body.classList.toggle('is-file-dragging', isActive);
    }

    private cancelFileDragDeactivation() {
        if (this.fileDragDeactivateTimerId === null) {
            return;
        }

        window.clearTimeout(this.fileDragDeactivateTimerId);
        this.fileDragDeactivateTimerId = null;
    }

    private scheduleFileDragDeactivation() {
        this.cancelFileDragDeactivation();
        this.fileDragDeactivateTimerId = window.setTimeout(() => {
            this.fileDragDeactivateTimerId = null;
            this.setFileDragActive(false);
        }, 80);
    }

    private clearFileDragState() {
        this.cancelFileDragDeactivation();
        this.setFileDragActive(false);
    }

    private async handleDrop(dataTransfer: DataTransfer) {
        this.shouldOfferResume = false;
        this.setStatus("分析拖曳內容...");

        try {
            const selection = await this.extractSelectionFromDataTransfer(dataTransfer);
            if (!selection) {
                this.setStatus("請先選取檔案或資料夾", "red");
                return;
            }

            this.applySelection(selection);
        } catch (error) {
            const message = error instanceof Error ? error.message : "無法讀取拖曳內容";
            this.setStatus(message, "red");
        }
    }

    private async openPicker(kind: UploadKind) {
        this.pickerKind = kind;

        if (kind === 'folder') {
            const selection = await this.trySelectFolderWithDirectoryPicker();
            if (selection) {
                this.applySelection(selection);
                return;
            }
        }

        this.fileInput.value = "";

        if (kind === 'folder') {
            this.fileInput.setAttribute('webkitdirectory', '');
            this.fileInput.setAttribute('directory', '');
        } else {
            this.fileInput.removeAttribute('webkitdirectory');
            this.fileInput.removeAttribute('directory');
        }

        this.fileInput.click();
    }

    private async trySelectFolderWithDirectoryPicker() {
        const directoryPickerWindow = window as DirectoryPickerWindow;
        if (typeof directoryPickerWindow.showDirectoryPicker !== 'function') {
            return null;
        }

        try {
            const directoryHandle = await directoryPickerWindow.showDirectoryPicker();
            if (!directoryHandle) {
                return null;
            }

            const rootName = this.normalizeRelativePath(directoryHandle.name || "資料夾");
            const items: SelectedUploadItem[] = [];
            await this.collectDirectoryHandleItems(directoryHandle, rootName, items);
            if (!items.length) {
                items.push(this.createEmptyFolderItem(rootName));
            }

            return this.createSelection(items, 'folder', rootName, rootName);
        } catch (error) {
            const directoryError = error as DOMException;
            if (directoryError && directoryError.name === 'AbortError') {
                return null;
            }

            throw error;
        }
    }

    private handlePickerSelect(e: Event) {
        if (!this.canSelectFile()) {
            return;
        }

        const input = e.target as HTMLInputElement;
        const files = input.files ? Array.from(input.files) : [];
        if (!files.length) {
            return;
        }

        if (this.pickerKind === 'folder') {
            this.applySelection(this.createFolderSelection(files));
            return;
        }

        this.applySelection(this.createPlainFileSelection(files, input.value));
    }

    private applySelection(selection: UploadSelection) {
        this.shouldOfferResume = false;
        this.selection = selection;
        this.resetResults();
        this.setStatus(this.initialStatusText);
        this.updateUI();
    }

    private createPlainFileSelection(files: File[], inputValue?: string) {
        const items = files.map(file => this.createSelectedUploadItem(file, file.name));
        let label = files.length === 1 ? this.getClientPath(files[0], inputValue) : `已選取 ${files.length} 個檔案`;
        if (inputValue && /^[a-zA-Z]:\\fakepath\\/i.test(inputValue) && files.length === 1) {
            label = files[0].name;
        }

        return this.createSelection(items, 'file', label, files.map(file => file.name).join('\n') || label);
    }

    private createFolderSelection(files: File[]) {
        const items = files.map(file => this.createSelectedUploadItem(file, this.normalizeRelativePath(file.webkitRelativePath || file.name)));
        const rootNames = this.getRootNames(items.map(item => item.relativePath));
        const label = this.getFolderLabel(rootNames, items.length);
        return this.createSelection(items, 'folder', label, rootNames.join('\n') || label);
    }

    private createSelection(items: SelectedUploadItem[], kind: UploadKind, label: string, tooltip: string): UploadSelection {
        return {
            kind,
            items,
            label,
            tooltip
        };
    }

    private createSelectedUploadItem(file: File, relativePath: string): SelectedUploadItem {
        const normalizedRelativePath = this.normalizeRelativePath(relativePath || file.name);
        return {
            itemType: 'file',
            file,
            relativePath: normalizedRelativePath,
            displayPath: normalizedRelativePath,
            uploadId: this.createUploadId(file, normalizedRelativePath),
            uploadedBytes: 0,
            progressBytes: 0,
            isCompleted: false
        };
    }

    private createEmptyFolderItem(relativePath: string): SelectedUploadItem {
        const normalizedRelativePath = this.normalizeRelativePath(relativePath);
        return {
            itemType: 'emptyFolder',
            file: null,
            relativePath: normalizedRelativePath,
            displayPath: normalizedRelativePath,
            uploadId: this.createUploadId(null, normalizedRelativePath),
            uploadedBytes: 0,
            progressBytes: 0,
            isCompleted: false
        };
    }

    private normalizeRelativePath(path: string) {
        return path
            .replace(/\\/g, '/')
            .split('/')
            .map(segment => segment.trim())
            .filter(segment => segment.length > 0)
            .join('/');
    }

    private getRootNames(relativePaths: string[]) {
        const names = new Set<string>();
        relativePaths.forEach(relativePath => {
            const [rootName] = relativePath.split('/');
            if (rootName) {
                names.add(rootName);
            }
        });

        return Array.from(names);
    }

    private getFolderLabel(rootNames: string[], fileCount: number) {
        if (rootNames.length === 0) {
            return `資料夾內容 (${fileCount} 個檔案)`;
        }

        if (rootNames.length === 1) {
            return rootNames[0];
        }

        return `${rootNames[0]} 等 ${rootNames.length} 個資料夾`;
    }

    private getClientPath(file: File, inputValue?: string) {
        if (inputValue) {
            if (/^[a-zA-Z]:\\fakepath\\/i.test(inputValue)) {
                return file.name;
            }

            return inputValue;
        }

        if (file.webkitRelativePath) {
            return file.webkitRelativePath;
        }

        return file.name;
    }

    private updateUI() {
        if (this.selection) {
            this.filePathBox.innerText = this.selection.label;
            this.filePathBox.title = this.selection.tooltip || this.selection.label;
        } else {
            this.filePathBox.innerText = this.initialFilePathText;
            this.filePathBox.title = this.initialFilePathText;
        }

        this.setProgress(0, false);
        this.refreshSubmitButton();
    }

    private resetUploadForm() {
        this.shouldOfferResume = false;
        this.selection = null;
        this.pickerKind = 'file';
        this.fileInput.value = "";
        this.fileInput.removeAttribute('webkitdirectory');
        this.fileInput.removeAttribute('directory');
        this.filePathBox.innerText = this.initialFilePathText;
        this.filePathBox.title = this.initialFilePathText;
        this.modeInputs.forEach(input => input.checked = input.value === this.initialModeValue);
        this.resetResults();

        if (this.isPasswordVisible) {
            this.isPasswordVisible = false;
            this.updatePasswordVisibility();
        }

        this.setProgress(0, false);
        this.refreshSubmitButton();
    }

    private resetResults() {
        this.uploadResultsBox.hidden = true;
        this.uploadResultsSummary.innerText = "";
        this.uploadResultsList.innerHTML = "";
    }

    private showFolderUploadResults(results: FolderUploadResult[]) {
        if (!results.length) {
            this.resetResults();
            return;
        }

        const createdCount = results.filter(result => result.summary.indexOf('新增') >= 0).length;
        const overwrittenCount = results.filter(result => result.summary.indexOf('覆蓋') >= 0).length;
        const renamedCount = results.filter(result => result.summary.indexOf('自動編號') >= 0).length;
        const skippedCount = results.filter(result => result.summary.indexOf('未上傳') >= 0).length;
        const folderCount = results.filter(result => result.itemType === 'emptyFolder').length;
        const summaryParts = [
            `新增 ${createdCount}`,
            `覆蓋 ${overwrittenCount}`,
            `自動編號 ${renamedCount}`,
            `未上傳 ${skippedCount}`
        ];

        if (folderCount > 0) {
            summaryParts.push(`空資料夾 ${folderCount}`);
        }

        this.uploadResultsSummary.innerText = summaryParts.join(' / ');
        this.uploadResultsList.innerHTML = "";

        results.forEach(result => {
            const item = document.createElement('li');
            item.className = `results-item${result.tone === 'warning' ? ' is-warning' : result.tone === 'error' ? ' is-error' : ''}`;
            item.innerText = `${result.relativePath}｜${result.summary}`;
            this.uploadResultsList.appendChild(item);
        });

        this.uploadResultsBox.hidden = false;
    }

    private setStatus(message: string, color = "#1e293b") {
        this.statusBox.innerText = message;
        this.statusBox.style.color = color;
    }

    private setProgress(percent: number, visible = true) {
        const safePercent = Math.max(0, Math.min(100, Math.round(percent)));
        this.progressBox.hidden = !visible;
        this.progressBar.style.width = `${safePercent}%`;
        this.progressText.innerText = `${safePercent}%`;
    }

    private getProgressPercent(uploadedBytes: number, totalBytes: number) {
        if (totalBytes <= 0) {
            return 0;
        }

        return (uploadedBytes / totalBytes) * 100;
    }

    private parseJson<T>(text: string) {
        if (!text) {
            return null;
        }

        try {
            return JSON.parse(text) as T;
        } catch {
            return null;
        }
    }

    private async readJsonResponse<T>(response: Response) {
        const text = await response.text();
        const data = this.parseJson<T & ErrorResponse>(text);

        if (!response.ok) {
            throw this.createRequestError(data?.message || text || "要求失敗", response.status);
        }

        if (!data) {
            throw this.createRequestError(text || "伺服器回應格式錯誤", response.status);
        }

        return data as T;
    }

    private createRequestError(message: string, statusCode?: number): RequestError {
        const error = new Error(message) as RequestError;
        if (typeof statusCode === 'number') {
            error.statusCode = statusCode;
        }

        return error;
    }

    private createUploadId(file: File | null, relativePath: string) {
        const fileSize = file ? file.size : 0;
        const lastModified = file ? file.lastModified : 0;
        const seed = `${relativePath}|${fileSize}|${lastModified}`;
        let hash = 0;

        for (let i = 0; i < seed.length; i++) {
            hash = ((hash << 5) - hash + seed.charCodeAt(i)) | 0;
        }

        return `upload_${Math.abs(hash)}_${fileSize}_${lastModified}`;
    }

    private getCurrentMode() {
        return (document.querySelector('input[name="mode"]:checked') as HTMLInputElement).value as ConflictMode;
    }

    private async validatePermission(pwd: string, mode: ConflictMode, uploadKind: UploadKind, parallelThreads: number) {
        const formData = new FormData();
        formData.append("pwd", pwd);
        formData.append("mode", mode);
        formData.append("uploadKind", uploadKind);
        formData.append("parallelThreads", `${parallelThreads}`);

        const response = await fetch(this.validateUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        const result = await response.text();
        return { response, result };
    }

    private async initializeUpload(item: SelectedUploadItem, pwd: string, mode: ConflictMode, uploadKind: UploadKind) {
        if (!item.file) {
            throw this.createRequestError("❌ 缺少檔案內容");
        }

        const formData = new FormData();
        formData.append("uploadId", item.uploadId);
        formData.append("fileName", item.file.name);
        formData.append("fileSize", `${item.file.size}`);
        formData.append("relativePath", item.relativePath);
        formData.append("pwd", pwd);
        formData.append("mode", mode);
        formData.append("uploadKind", uploadKind);

        const response = await fetch(this.initializeUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        return await this.readJsonResponse<UploadSession>(response);
    }

    private async completeUpload(uploadId: string, pwd: string, mode: ConflictMode, uploadKind: UploadKind) {
        const formData = new FormData();
        formData.append("uploadId", uploadId);
        formData.append("pwd", pwd);
        formData.append("mode", mode);
        formData.append("uploadKind", uploadKind);

        const response = await fetch(this.completeUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        return await this.readJsonResponse<CompleteUploadResult>(response);
    }

    private async createFolder(relativePath: string, pwd: string, mode: ConflictMode, uploadKind: UploadKind) {
        const formData = new FormData();
        formData.append("relativePath", relativePath);
        formData.append("pwd", pwd);
        formData.append("mode", mode);
        formData.append("uploadKind", uploadKind);

        const response = await fetch(this.createFolderUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        return await this.readJsonResponse<CreateFolderResult>(response);
    }

    private createFolderResultItem(item: SelectedUploadItem, result: CreateFolderResult): FolderUploadResult {
        const summary = result.action === 'folderExisting'
            ? '空資料夾已存在'
            : '已建立空資料夾';

        return {
            itemType: item.itemType,
            relativePath: result.relativePath || item.displayPath,
            tone: result.action === 'folderExisting' ? 'warning' : 'success',
            summary
        };
    }

    private createCompletedFileResult(item: SelectedUploadItem, result: CompleteUploadResult): FolderUploadResult {
        let summary = '新增';
        let tone: FolderUploadResult['tone'] = 'success';

        if (result.action === 'overwritten') {
            summary = '覆蓋';
        } else if (result.action === 'renamed') {
            summary = `自動編號：${result.originalRelativePath} → ${result.relativePath}`;
        }

        return {
            itemType: item.itemType,
            relativePath: result.relativePath || item.displayPath,
            tone,
            summary
        };
    }

    private createSkippedConflictResult(item: SelectedUploadItem, error: RequestError): FolderUploadResult {
        return {
            itemType: item.itemType,
            relativePath: item.displayPath,
            tone: 'warning',
            summary: `未上傳：${error.message || '檔案已存在'}`
        };
    }

    private isSkippableFolderConflict(error: RequestError, mode: ConflictMode) {
        if (mode !== 'prohibit' || error.statusCode !== 409) {
            return false;
        }

        const message = error.message || '';
        return message.indexOf('檔案已存在') >= 0 || message.indexOf('已存在同名檔案') >= 0;
    }

    private async clearValidatedUploadSession() {
        await fetch(this.clearSessionUrl, {
            method: 'POST',
            credentials: 'same-origin'
        });
    }

    private uploadChunk(formData: FormData, onProgress: (loadedBytes: number) => void) {
        return new Promise<ChunkUploadResult>((resolve, reject) => {
            const xhr = new XMLHttpRequest();

            xhr.open('POST', this.uploadChunkUrl, true);
            xhr.withCredentials = true;

            xhr.upload.addEventListener('progress', (e) => {
                if (!e.lengthComputable || e.total <= 0) {
                    return;
                }

                onProgress(e.loaded);
            });

            xhr.onload = () => {
                const result = this.parseJson<ChunkUploadResult & ErrorResponse>(xhr.responseText);
                if (xhr.status < 200 || xhr.status >= 300) {
                    reject(this.createRequestError(result?.message || xhr.responseText || "分段上傳失敗", xhr.status));
                    return;
                }

                if (!result) {
                    reject(this.createRequestError("伺服器回應格式錯誤", xhr.status));
                    return;
                }

                resolve(result);
            };

            xhr.onerror = () => reject(this.createRequestError("網路傳輸失敗"));
            xhr.onabort = () => reject(this.createRequestError("上傳已取消"));

            xhr.send(formData);
        });
    }

    private async uploadChunkWithRetry(
        createFormData: () => FormData,
        onProgress: (loadedBytes: number) => void,
        fileLabel: string) {
        let attempt = 0;
        while (true) {
            try {
                return await this.uploadChunk(createFormData(), onProgress);
            } catch (error) {
                const requestError = error as RequestError;
                const shouldRetry = attempt < this.chunkRetryCount - 1 && this.isRetryableChunkError(requestError);
                if (!shouldRetry) {
                    throw requestError;
                }

                attempt += 1;
                this.setStatus(`傳輸中斷，準備重試 (${attempt}/${this.chunkRetryCount - 1})：${fileLabel}`);
                await this.delay(this.chunkRetryDelayMs);
            }
        }
    }

    private isRetryableChunkError(error: RequestError) {
        return typeof error.statusCode !== 'number' || error.statusCode >= 500 || error.statusCode === 0;
    }

    private delay(durationMs: number) {
        return new Promise<void>(resolve => window.setTimeout(resolve, durationMs));
    }

    private async uploadFileByChunks(item: SelectedUploadItem, session: UploadSession, pwd: string, mode: ConflictMode, uploadKind: UploadKind, items: SelectedUploadItem[], totalBatchBytes: number, fileIndex: number, totalFiles: number) {
        if (!item.file) {
            throw this.createRequestError("❌ 缺少檔案內容");
        }

        const file = item.file;
        const totalBytes = file.size;
        const totalChunks = Math.max(1, Math.ceil(totalBytes / session.chunkSize));
        let uploadedBytes = Math.max(0, Math.min(session.uploadedBytes, totalBytes));

        item.uploadedBytes = uploadedBytes;
        item.progressBytes = uploadedBytes;
        this.updateBatchProgress(items, totalBatchBytes, item.displayPath, fileIndex, totalFiles);

        while (uploadedBytes < totalBytes) {
            const chunkStart = uploadedBytes;
            const chunkIndex = Math.floor(chunkStart / session.chunkSize);
            const chunkEnd = Math.min(chunkStart + session.chunkSize, totalBytes);

            const result = await this.uploadChunkWithRetry(() => {
                const formData = new FormData();
                formData.append("uploadId", session.uploadId);
                formData.append("chunkIndex", `${chunkIndex}`);
                formData.append("totalChunks", `${totalChunks}`);
                formData.append("chunkStart", `${chunkStart}`);
                formData.append("pwd", pwd);
                formData.append("mode", mode);
                formData.append("uploadKind", uploadKind);
                formData.append("chunk", file.slice(chunkStart, chunkEnd), file.name);
                return formData;
            }, (loadedBytes) => {
                item.progressBytes = Math.max(item.uploadedBytes, Math.min(chunkStart + loadedBytes, totalBytes));
                this.updateBatchProgress(items, totalBatchBytes, item.displayPath, fileIndex, totalFiles);
            }, item.displayPath);

            uploadedBytes = Math.max(uploadedBytes, result.uploadedBytes);
            item.uploadedBytes = uploadedBytes;
            item.progressBytes = uploadedBytes;
            this.updateBatchProgress(items, totalBatchBytes, item.displayPath, fileIndex, totalFiles);
        }
    }

    private updateBatchProgress(items: SelectedUploadItem[], totalBytes: number, fileLabel: string, fileIndex: number, totalFiles: number, statusPrefix = "上傳中") {
        const percent = totalBytes > 0
            ? this.getProgressPercent(this.getUploadedProgressBytes(items), totalBytes)
            : this.getProgressPercent(this.getCompletedItemCount(items), totalFiles);
        this.setProgress(percent, true);
        this.setStatus(`${statusPrefix} (${fileIndex}/${totalFiles}) ${fileLabel} · ${Math.round(percent)}%`);
    }

    private getTotalBytes(items: SelectedUploadItem[]) {
        return items.reduce((sum, item) => sum + (item.file ? item.file.size : 0), 0);
    }

    private getUploadedProgressBytes(items: SelectedUploadItem[]) {
        return items.reduce((sum, item) => sum + (item.file ? Math.max(0, Math.min(item.progressBytes, item.file.size)) : 0), 0);
    }

    private getCompletedItemCount(items: SelectedUploadItem[]) {
        return items.filter(item => item.isCompleted).length;
    }

    private markItemCompleted(item: SelectedUploadItem) {
        item.isCompleted = true;
        const completedBytes = item.file ? item.file.size : 0;
        item.uploadedBytes = completedBytes;
        item.progressBytes = completedBytes;
    }

    private async processUploadItem(item: SelectedUploadItem, items: SelectedUploadItem[], pwd: string, mode: ConflictMode, uploadKind: UploadKind, totalBytes: number, itemIndex: number, totalFiles: number) {
        if (item.isCompleted) {
            return null;
        }

        if (item.itemType === 'emptyFolder') {
            this.setStatus(`建立資料夾 (${itemIndex + 1}/${totalFiles}) ${item.displayPath}`);

            try {
                const folderResult = await this.createFolder(item.relativePath, pwd, mode, uploadKind);
                this.markItemCompleted(item);
                this.updateBatchProgress(items, totalBytes, item.displayPath, itemIndex + 1, totalFiles, "已完成");
                return this.createFolderResultItem(item, folderResult);
            } catch (error) {
                const requestError = error as RequestError;
                if (uploadKind === 'folder' && this.isSkippableFolderConflict(requestError, mode)) {
                    this.markItemCompleted(item);
                    this.updateBatchProgress(items, totalBytes, item.displayPath, itemIndex + 1, totalFiles, "已略過");
                    return this.createSkippedConflictResult(item, requestError);
                }

                throw error;
            }
        }

        try {
            const session = await this.initializeUpload(item, pwd, mode, uploadKind);
            item.uploadedBytes = session.uploadedBytes;
            item.progressBytes = session.uploadedBytes;
            this.updateBatchProgress(items, totalBytes, item.displayPath, itemIndex + 1, totalFiles, session.isResuming && session.uploadedBytes > 0 ? "續傳中" : "準備上傳");

            await this.uploadFileByChunks(item, session, pwd, mode, uploadKind, items, totalBytes, itemIndex + 1, totalFiles);
            const completeResult = await this.completeUpload(session.uploadId, pwd, mode, uploadKind);
            this.markItemCompleted(item);
            this.updateBatchProgress(items, totalBytes, item.displayPath, itemIndex + 1, totalFiles, "已完成");
            return this.createCompletedFileResult(item, completeResult);
        } catch (error) {
            const requestError = error as RequestError;
            if (uploadKind === 'folder' && this.isSkippableFolderConflict(requestError, mode)) {
                this.markItemCompleted(item);
                this.updateBatchProgress(items, totalBytes, item.displayPath, itemIndex + 1, totalFiles, "已略過");
                return this.createSkippedConflictResult(item, requestError);
            }

            throw error;
        }
    }

    private async uploadFolderItemsInParallel(items: SelectedUploadItem[], pwd: string, mode: ConflictMode, uploadKind: UploadKind, parallelThreads: number, totalBytes: number, totalFiles: number) {
        const folderResults: Array<FolderUploadResult | null> = new Array(items.length);
        let nextIndex = 0;
        let firstError: RequestError | null = null;

        const getNextIndex = () => {
            while (nextIndex < items.length && items[nextIndex].isCompleted) {
                nextIndex += 1;
            }

            if (nextIndex >= items.length) {
                return -1;
            }

            const currentIndex = nextIndex;
            nextIndex += 1;
            return currentIndex;
        };

        const worker = async () => {
            while (!firstError) {
                const currentIndex = getNextIndex();
                if (currentIndex < 0) {
                    return;
                }

                try {
                    folderResults[currentIndex] = await this.processUploadItem(items[currentIndex], items, pwd, mode, uploadKind, totalBytes, currentIndex, totalFiles);
                } catch (error) {
                    firstError = error as RequestError;
                    return;
                }
            }
        };

        const workerCount = Math.max(1, Math.min(parallelThreads, items.length));
        await Promise.all(Array.from({ length: workerCount }, () => worker()));

        if (firstError) {
            throw firstError;
        }

        return folderResults.filter((result): result is FolderUploadResult => result !== null);
    }

    private async upload() {
        if (!this.canSubmit()) {
            return;
        }

        if (!this.selection || this.selection.items.length === 0) {
            this.setStatus("請先選取檔案或資料夾", "red");
            return;
        }

        const pwd = this.pwdInput.value;
        const mode = this.getCurrentMode();
        const uploadKind = this.selection.kind;
        const totalFiles = this.selection.items.length;
        const totalBytes = this.getTotalBytes(this.selection.items);
        const parallelThreads = uploadKind === 'folder' ? this.getFolderParallelThreads() : 1;
        let folderResults: FolderUploadResult[] = [];
        let hasValidatedSession = false;

        this.setUploadingState(true);
        this.shouldOfferResume = false;
        this.setProgress(0, false);
        this.setStatus("驗證中...");

        try {
            const validation = await this.validatePermission(pwd, mode, uploadKind, parallelThreads);
            if (!validation.response.ok) {
                if (validation.response.status === 429) {
                    this.startLockout(this.validatePermissionLockoutMs);
                }

                this.setProgress(0, false);
                this.setStatus(validation.result, "red");
                return;
            }

            hasValidatedSession = true;
            if (uploadKind === 'folder') {
                this.setStatus(`準備啟動 ${parallelThreads} 條線程...`);
                folderResults = await this.uploadFolderItemsInParallel(this.selection.items, pwd, mode, uploadKind, parallelThreads, totalBytes, totalFiles);
            } else {
                const result = await this.processUploadItem(this.selection.items[0], this.selection.items, pwd, mode, uploadKind, totalBytes, 0, totalFiles);
                folderResults = result ? [result] : [];
            }

            this.setProgress(100, true);
            const successMessage = uploadKind === 'folder'
                ? `✅ 資料夾處理完成：${this.selection.label}`
                : totalFiles === 1
                    ? `✅ 成功上傳：${this.selection.items[0].displayPath}`
                    : `✅ 成功上傳 ${totalFiles} 個檔案`;
            if (uploadKind === 'folder') {
                this.resetUploadForm();
                this.showFolderUploadResults(folderResults);
            } else {
                this.resetUploadForm();
            }
            this.setStatus(successMessage, "green");
        } catch (err) {
            this.shouldOfferResume = true;
            const message = err instanceof Error ? err.message : "上傳失敗";
            this.setStatus(`${message}。重新按上傳可續傳`, "red");
        } finally {
            if (hasValidatedSession) {
                try {
                    await this.clearValidatedUploadSession();
                } catch {
                }
            }

            this.setUploadingState(false);
        }
    }

    private async extractSelectionFromDataTransfer(dataTransfer: DataTransfer) {
        const itemEntries = Array.from(dataTransfer.items || []).map(item => this.getEntry(item)).filter((entry): entry is FileSystemEntry => entry !== null);
        if (itemEntries.some(entry => entry.isDirectory)) {
            const items: SelectedUploadItem[] = [];
            for (const entry of itemEntries) {
                await this.collectEntryItems(entry, "", items);
            }

            if (!items.length) {
                return null;
            }

            const rootNames = Array.from(new Set(itemEntries.map(entry => entry.name).filter(name => !!name)));
            return this.createSelection(items, 'folder', this.getFolderLabel(rootNames, items.length), rootNames.join('\n'));
        }

        const files = Array.from(dataTransfer.files || []);
        if (!files.length) {
            return null;
        }

        return this.createPlainFileSelection(files);
    }

    private getEntry(item: DataTransferItem) {
        const dataTransferItem = item as DataTransferItemWithEntry;
        return dataTransferItem.webkitGetAsEntry ? dataTransferItem.webkitGetAsEntry() : null;
    }

    private async collectEntryItems(entry: FileSystemEntry, parentPath: string, items: SelectedUploadItem[]) {
        if (entry.isFile) {
            const file = await this.readFileEntry(entry as FileSystemFileEntry);
            const relativePath = this.normalizeRelativePath(parentPath ? `${parentPath}/${entry.name}` : entry.name);
            items.push(this.createSelectedUploadItem(file, relativePath));
            return;
        }

        if (!entry.isDirectory) {
            return;
        }

        const directoryEntry = entry as FileSystemDirectoryEntry;
        const entries = await this.readAllDirectoryEntries(directoryEntry.createReader());
        const nextParentPath = this.normalizeRelativePath(parentPath ? `${parentPath}/${entry.name}` : entry.name);
        if (!entries.length) {
            items.push(this.createEmptyFolderItem(nextParentPath));
            return;
        }

        for (const childEntry of entries) {
            await this.collectEntryItems(childEntry, nextParentPath, items);
        }
    }

    private async collectDirectoryHandleItems(directoryHandle: any, currentPath: string, items: SelectedUploadItem[]) {
        const entries: any[] = [];
        for await (const entry of directoryHandle.values()) {
            entries.push(entry);
        }

        if (!entries.length) {
            items.push(this.createEmptyFolderItem(currentPath));
            return;
        }

        for (const entry of entries) {
            if (entry.kind === 'file') {
                const file = await entry.getFile();
                items.push(this.createSelectedUploadItem(file, this.normalizeRelativePath(`${currentPath}/${entry.name}`)));
                continue;
            }

            if (entry.kind === 'directory') {
                await this.collectDirectoryHandleItems(entry, this.normalizeRelativePath(`${currentPath}/${entry.name}`), items);
            }
        }
    }

    private readFileEntry(entry: FileSystemFileEntry) {
        return new Promise<File>((resolve, reject) => {
            entry.file(resolve, reject);
        });
    }

    private async readAllDirectoryEntries(reader: FileSystemDirectoryReader) {
        const entries: FileSystemEntry[] = [];

        while (true) {
            const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => {
                reader.readEntries(resolve, reject);
            });

            if (!batch.length) {
                break;
            }

            entries.push(...batch);
        }

        return entries;
    }
}();
