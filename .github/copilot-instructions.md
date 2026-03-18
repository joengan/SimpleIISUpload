# Copilot Instructions

## Project Guidelines
- For the upload UI, if the browser-provided client path starts with C:\fakepath\, display only the filename instead of the fake path.
- For the upload UI, prefer the password visibility toggle to look like an embedded eye icon inside the input rather than a separate text button. The password visibility eye button hover should not show a gray circular background; keep the hover background transparent and rely on cursor or subtle color change instead.
- For the upload UI, during upload, disable all interactive actions including password edits, mode changes, drag-and-drop, and file reselection. Only after interruption/failure/timeout should the main button reopen as '繼續上傳'. After successful completion, reset the selected content and button state, but do not clear the password field so users do not need to re-enter it every time. The UI should reset to the initial empty state to avoid accidental duplicate uploads.
- For the upload UI, when the validation lockout message occurs after repeated password failures, disable the submit button and block Enter submission until the one-minute cooldown auto-expires.
- When a CSS change appears broken in this project, refer back to the previous version's styling structure before deciding the fix, and verify the actual impact on the upload UI.
- Avoid installing additional packages when configuring this project unless absolutely necessary.
- In the upload UI, only truly clickable elements should show the pointer cursor. The non-clickable drop zone should not look clickable once direct click-to-open is removed; the file/folder picker buttons should explicitly show the pointer cursor instead.
- For folder uploads in this project, duplicate handling differs from single-file uploads: after the batch completes, show a full per-item result list. In prohibit mode, list files not uploaded because they already exist; in overwrite mode, indicate whether each file was newly added or overwritten; in auto mode, show both the original name/path and the new auto-numbered name/path.

## GitHub Actions
- Clarify that Node.js warnings in GitHub Actions refer to the action runtime, not the .NET project itself.

## Language Preference
- For project discussions, use Traditional Chinese responses.