/** @type {import('tailwindcss').Config} */
module.exports = {
    darkMode: 'class',
    content: [
        './Components/**/*.razor',
        './**/*.html',
        './**/*.cs',
        './wwwroot/js/**/*.js'
    ],
    theme: {
        extend: {
            colors: {
                sidebar: 'var(--bg-sidebar)',
                mainbg: 'var(--bg-primary)',
                surface: 'var(--bg-surface)',
                'surface2': 'var(--bg-surface2)',
                'user-bubble': 'var(--bg-surface)',
                primary: 'var(--text-primary)',
                secondary: 'var(--text-secondary)',
                muted: 'var(--text-muted)',
                accent: 'rgb(var(--accent) / <alpha-value>)',
                'accent-hover': 'rgb(var(--accent-hover) / <alpha-value>)',
                border: 'var(--border-color)',
            },
            maxWidth: { chat: '900px' },
            fontFamily: { sans: ['"Segoe UI"', '"Helvetica Neue"', 'Arial', 'sans-serif'] },
        }
    },
    plugins: []
};
