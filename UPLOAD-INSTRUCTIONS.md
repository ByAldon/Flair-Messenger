# Uploading the Flair Messenger website

The website is fully static and does not require a build step.

1. Upload `index.html`, `styles.css`, `script.js` and `.nojekyll` to the folder or branch used by GitHub Pages.
2. In the repository, open **Settings → Pages**.
3. Under **Build and deployment**, select **Deploy from a branch**.
4. Select the branch and folder containing these files, then save.

The website links directly to the official installer and portable release assets. It also checks the GitHub Releases API in the visitor's browser and automatically updates both download links when a newer release is available.

Current fallback downloads:

- Installer: `Flair-Messenger-Setup-v0.4.46.exe`
- Portable: `Flair-Messenger-Portable-v0.4.46.zip` (contains `FlairMessenger.exe`)
