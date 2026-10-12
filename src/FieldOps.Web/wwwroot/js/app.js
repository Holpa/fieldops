// Small browser helpers. Everything else runs on the server.
window.fieldops = {
    // Flips light/dark and remembers the choice in a cookie, so the server renders the next page in the same
    // theme from the start (no flash of the wrong colours).
    toggleTheme() {
        const root = document.documentElement;
        const next = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
        root.setAttribute('data-bs-theme', next);
        const secure = location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = `fieldops-theme=${next}; path=/; max-age=31536000; SameSite=Lax${secure}`;
    },

    toggleNav(open) {
        const isOpen = document.body.classList.toggle('nav-open', open);
        document.querySelector('.nav-toggle')?.setAttribute('aria-expanded', String(isOpen));
    },

    scrollToBottom(element) {
        if (element) element.scrollTop = element.scrollHeight;
    },

    focusById(id) {
        const element = document.getElementById(id);
        if (element) element.focus();
    },
};

// Close the mobile menu after choosing a page.
document.addEventListener('click', (event) => {
    if (event.target.closest('.sidebar a')) window.fieldops.toggleNav(false);
});

// The skip link moves keyboard focus to the page content. With <base href="/"> a plain "#main" link would
// resolve to "/#main" and take the user to the home page, so the click is handled here before Blazor sees it.
window.addEventListener('click', (event) => {
    if (!event.target.closest('.skip-link')) return;
    event.preventDefault();
    event.stopPropagation();
    window.fieldops.focusById('main');
}, true);

// Firefox only starts a drag when the drag carries some data. Blazor can't set it from C#, so it is set here
// for board cards; the C# drag handlers still run as normal.
document.addEventListener('dragstart', (event) => {
    const card = event.target.closest && event.target.closest('.board-card');
    if (card && event.dataTransfer) {
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', card.dataset.id || '');
    }
}, true);

// Header buttons are wired here instead of inline onclick attributes, so the CSP can forbid inline script.
document.addEventListener('click', (event) => {
    if (event.target.closest('[data-action="toggle-theme"]')) window.fieldops.toggleTheme();
    else if (event.target.closest('[data-action="toggle-nav"]')) window.fieldops.toggleNav();
    else if (event.target.closest('.sidebar-backdrop')) window.fieldops.toggleNav(false);
});
