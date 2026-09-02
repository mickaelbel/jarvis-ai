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
                sidebar: '#171717',
                mainbg: '#212121',
                surface: '#2f2f2f',
                'user-bubble': '#2f2f2f',
                primary: '#ececec',
                secondary: '#b4b4b4',
                muted: '#6b6b6b',
                accent: 'rgb(var(--accent) / <alpha-value>)',
                'accent-hover': 'rgb(var(--accent-hover) / <alpha-value>)',
                border: '#2f2f2f',
            },
            maxWidth: { chat: '900px' },
            fontFamily: { sans: ['"Segoe UI"', '"Helvetica Neue"', 'Arial', 'sans-serif'] },
        }
    },
    plugins: []
};
