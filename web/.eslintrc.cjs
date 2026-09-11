module.exports = {
  root: true,
  env: { browser: true, es2020: true },
  extends: [
    'eslint:recommended',
    'plugin:@typescript-eslint/recommended',
    'plugin:react-hooks/recommended',
  ],
  ignorePatterns: ['dist', '.eslintrc.cjs', 'routeTree.gen.ts'],
  parser: '@typescript-eslint/parser',
  plugins: ['react-refresh'],
  rules: {
    'react-refresh/only-export-components': [
      'warn',
      { allowConstantExport: true },
    ],
  },
  overrides: [
    {
      // shadcn/ui's generated components intentionally export a helper
      // (a cva variant function, a useFormField hook) alongside the
      // component itself — the library's own convention, not something
      // this project's code introduced. Fast Refresh still works for these
      // files in practice; the rule just can't tell that apart from a
      // genuine mixed-export mistake.
      files: ['src/components/ui/**/*.tsx'],
      rules: { 'react-refresh/only-export-components': 'off' },
    },
  ],
};
