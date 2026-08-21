const menuButton = document.querySelector('.menu-toggle');
const siteNav = document.querySelector('.site-nav');

menuButton?.addEventListener('click', () => {
  const open = siteNav.classList.toggle('open');
  menuButton.setAttribute('aria-expanded', String(open));
  menuButton.querySelector('.sr-only').textContent = open ? 'Close navigation' : 'Open navigation';
});

siteNav?.addEventListener('click', (event) => {
  if (event.target.closest('a')) {
    siteNav.classList.remove('open');
    menuButton?.setAttribute('aria-expanded', 'false');
  }
});

document.querySelector('[data-year]').textContent = new Date().getFullYear();

fetch('https://api.github.com/repos/ByAldon/Flair-Messenger/releases/latest', {
  headers: { Accept: 'application/vnd.github+json' }
})
  .then((response) => response.ok ? response.json() : Promise.reject())
  .then((release) => {
    if (/^v?\d+\.\d+\.\d+$/.test(release.tag_name)) {
      document.querySelectorAll('[data-release]').forEach((node) => {
        node.textContent = release.tag_name.startsWith('v') ? release.tag_name : `v${release.tag_name}`;
      });
    }

    const installer = release.assets?.find((asset) => /setup.*\.exe$/i.test(asset.name));
    const portable = release.assets?.find((asset) => /portable.*\.zip$/i.test(asset.name));
    document.querySelectorAll('[data-download="installer"]').forEach((link) => {
      if (installer?.browser_download_url) link.href = installer.browser_download_url;
    });
    document.querySelectorAll('[data-download="portable"]').forEach((link) => {
      if (portable?.browser_download_url) link.href = portable.browser_download_url;
    });
  })
  .catch(() => {
    // Keep the release version bundled with the page when GitHub is unavailable.
  });
