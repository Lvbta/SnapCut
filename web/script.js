// SnapCut landing page: light interactions
(function () {
  const btn = document.getElementById('download-btn');
  if (!btn) return;

  // Fetch latest release info from GitHub to keep the download link fresh
  fetch('https://api.github.com/repos/Lvbta/SnapCut/releases/latest')
    .then(r => r.ok ? r.json() : null)
    .then(data => {
      if (!data || !data.tag_name) return;
      const version = data.tag_name.replace(/^v/, '');
      const setup = data.assets.find(a => /SnapCut-Setup-v[\d.]+\.exe$/i.test(a.name));
      const zip = data.assets.find(a => /SnapCut-v[\d.]+-portable\.zip$/i.test(a.name));
      if (setup) btn.href = setup.browser_download_url;
      const small = btn.querySelector('small');
      if (small) small.textContent = `v${version} · Windows 10 / 11`;
      if (zip) {
        const meta = document.querySelector('.meta a');
        if (meta) meta.href = zip.browser_download_url;
      }
    })
    .catch(() => { /* keep the hard-coded link as fallback */ });
})();
