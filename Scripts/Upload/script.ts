// 定義傳輸類型
type ConflictMode = "prohibit" | "overwrite" | "auto";
type UploadSession = {
    uploadId: string;
    fileName: string;
    uploadedBytes: number;
    chunkSize: number;
    maxFileSize: number;
    isResuming: boolean;
};
type ChunkUploadResult = { uploadedBytes: number; message: string; isCompleted: boolean; };
type CompleteUploadResult = { message: string; };
type ErrorResponse = { message?: string; };

new class {
    private file: File | null = null;
    private filePath = "";
    private uploadCard = document.getElementById('uploadCard') as HTMLDivElement;
    private validateUrl = (this.uploadCard.dataset.validateUrl || "ValidatePermission");
    private initializeUrl = (this.uploadCard.dataset.initUrl || "InitializeUpload");
    private uploadChunkUrl = (this.uploadCard.dataset.uploadChunkUrl || "UploadChunk");
    private completeUrl = (this.uploadCard.dataset.completeUrl || "CompleteUpload");
    private dropZone = document.getElementById('dropZone') as HTMLDivElement;
    private fileInput = document.getElementById('fileInput') as HTMLInputElement;
    private filePathBox = document.getElementById('filePath') as HTMLSpanElement;
    private progressBox = document.getElementById('uploadProgress') as HTMLDivElement;
    private progressBar = document.getElementById('uploadProgressBar') as HTMLDivElement;
    private progressText = document.getElementById('uploadProgressText') as HTMLSpanElement;
    private statusBox = document.getElementById('status') as HTMLParagraphElement;
    private btn = document.getElementById('uploadBtn') as HTMLButtonElement;

    constructor() {
        this.initEvents();
    }

    private initEvents() {
        this.dropZone.onclick = () => this.fileInput.click();
        this.fileInput.onchange = (e) => this.handleFileSelect(e);

        ['dragenter', 'dragover'].forEach(name => {
            this.dropZone.addEventListener(name, (e) => {
                e.preventDefault();
                this.dropZone.classList.add('active');
            });
        });

        ['dragleave', 'drop'].forEach(name => {
            this.dropZone.addEventListener(name, (e) => {
                e.preventDefault();
                this.dropZone.classList.remove('active');
            });
        });

        this.dropZone.addEventListener('drop', (e) => {
            const files = e.dataTransfer?.files;
            if (files && files.length > 0) {
                this.file = files[0];
                this.filePath = this.getClientPath(this.file);
                this.updateUI();
            }
        });

        this.btn.onclick = () => this.upload();
    }

    private handleFileSelect(e: Event) {
        const input = e.target as HTMLInputElement;
        if (input.files && input.files.length > 0) {
            this.file = input.files[0];
            this.filePath = this.getClientPath(this.file, input.value);
            this.updateUI();
        }
    }

    private getClientPath(file: File, inputValue?: string) {
        if (inputValue) {
            if (/^[a-zA-Z]:\\fakepath\\/i.test(inputValue)) return file.name;
            return inputValue;
        }
        if (file.webkitRelativePath) return file.webkitRelativePath;
        return file.name;
    }

    private updateUI() {
        if (this.file) {
            this.filePathBox.innerText = this.filePath;
            this.filePathBox.title = this.filePath;
        }

        this.setProgress(0, false);
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
            throw new Error(data?.message || text || "要求失敗");
        }

        if (!data) {
            throw new Error(text || "伺服器回應格式錯誤");
        }

        return data as T;
    }

    private createUploadId(file: File) {
        const seed = `${file.name}|${file.size}|${file.lastModified}`;
        let hash = 0;

        for (let i = 0; i < seed.length; i++) {
            hash = ((hash << 5) - hash + seed.charCodeAt(i)) | 0;
        }

        return `upload_${Math.abs(hash)}_${file.size}_${file.lastModified}`;
    }

    private async validatePermission(pwd: string, mode: ConflictMode) {
        const formData = new FormData();
        formData.append("pwd", pwd);
        formData.append("mode", mode);

        const response = await fetch(this.validateUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        const result = await response.text();
        return { response, result };
    }

    private async initializeUpload(file: File, pwd: string, mode: ConflictMode) {
        const formData = new FormData();
        formData.append("uploadId", this.createUploadId(file));
        formData.append("fileName", file.name);
        formData.append("fileSize", `${file.size}`);
        formData.append("pwd", pwd);
        formData.append("mode", mode);

        const response = await fetch(this.initializeUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        return await this.readJsonResponse<UploadSession>(response);
    }

    private async completeUpload(uploadId: string, pwd: string, mode: ConflictMode) {
        const formData = new FormData();
        formData.append("uploadId", uploadId);
        formData.append("pwd", pwd);
        formData.append("mode", mode);

        const response = await fetch(this.completeUrl, {
            method: 'POST',
            body: formData,
            credentials: 'same-origin'
        });

        return await this.readJsonResponse<CompleteUploadResult>(response);
    }

    private uploadChunk(formData: FormData, uploadedBytesBefore: number, totalBytes: number) {
        return new Promise<ChunkUploadResult>((resolve, reject) => {
            const xhr = new XMLHttpRequest();

            xhr.open('POST', this.uploadChunkUrl, true);
            xhr.withCredentials = true;

            xhr.upload.addEventListener('progress', (e) => {
                if (!e.lengthComputable || e.total <= 0 || totalBytes <= 0) {
                    return;
                }

                const percent = this.getProgressPercent(uploadedBytesBefore + e.loaded, totalBytes);
                this.setProgress(percent);
                this.setStatus(`上傳中... ${Math.round(percent)}%`);
            });

            xhr.onload = () => {
                const result = this.parseJson<ChunkUploadResult & ErrorResponse>(xhr.responseText);
                if (xhr.status < 200 || xhr.status >= 300) {
                    reject(new Error(result?.message || xhr.responseText || "分段上傳失敗"));
                    return;
                }

                if (!result) {
                    reject(new Error("伺服器回應格式錯誤"));
                    return;
                }

                resolve(result);
            };

            xhr.onerror = () => reject(new Error("網路傳輸失敗"));
            xhr.onabort = () => reject(new Error("上傳已取消"));

            xhr.send(formData);
        });
    }

    private async uploadFileByChunks(file: File, session: UploadSession, pwd: string, mode: ConflictMode) {
        const totalBytes = file.size;
        const totalChunks = Math.max(1, Math.ceil(totalBytes / session.chunkSize));
        let uploadedBytes = Math.max(0, Math.min(session.uploadedBytes, totalBytes));

        while (uploadedBytes < totalBytes) {
            const chunkIndex = Math.floor(uploadedBytes / session.chunkSize);
            const chunkEnd = Math.min(uploadedBytes + session.chunkSize, totalBytes);
            const formData = new FormData();
            formData.append("uploadId", session.uploadId);
            formData.append("chunkIndex", `${chunkIndex}`);
            formData.append("totalChunks", `${totalChunks}`);
            formData.append("chunkStart", `${uploadedBytes}`);
            formData.append("pwd", pwd);
            formData.append("mode", mode);
            formData.append("chunk", file.slice(uploadedBytes, chunkEnd), file.name);

            const result = await this.uploadChunk(formData, uploadedBytes, totalBytes);
            uploadedBytes = Math.max(uploadedBytes, result.uploadedBytes);
            const percent = this.getProgressPercent(uploadedBytes, totalBytes);
            this.setProgress(percent);
            this.setStatus(`上傳中... ${Math.round(percent)}%`);
        }
    }

    private async upload() {
        if (!this.file) {
            this.setStatus("請先選取檔案", "red");
            return;
        }

        const pwd = (document.getElementById('pwd') as HTMLInputElement).value;
        const mode = (document.querySelector('input[name="mode"]:checked') as HTMLInputElement).value as ConflictMode;

        this.btn.disabled = true;
        this.setProgress(0, false);
        this.setStatus("驗證中...");

        try {
            const validation = await this.validatePermission(pwd, mode);
            if (!validation.response.ok) {
                this.setProgress(0, false);
                this.setStatus(validation.result, "red");
                return;
            }

            const session = await this.initializeUpload(this.file, pwd, mode);
            const startPercent = this.getProgressPercent(session.uploadedBytes, this.file.size);
            this.setProgress(startPercent, true);
            this.setStatus(session.isResuming && session.uploadedBytes > 0
                ? `偵測到中斷續傳，從 ${Math.round(startPercent)}% 繼續...`
                : "上傳中... 0%");

            await this.uploadFileByChunks(this.file, session, pwd, mode);
            const response = await this.completeUpload(session.uploadId, pwd, mode);
            this.setProgress(100);
            this.setStatus(response.message, "green");
        } catch (err) {
            const message = err instanceof Error ? err.message : "上傳失敗";
            this.setStatus(`${message}。重新按上傳可續傳`, "red");
        } finally {
            this.btn.disabled = false;
        }
    }
}();
