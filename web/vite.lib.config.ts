// Library build for the ui component set — separate from vite.config.ts
// (the app build). Produces a real importable entry + rolled-up .d.ts so
// tools that expect a packaged component library (design-sync) have
// something real to point at, without changing how the app itself builds.
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import dts from 'vite-plugin-dts';
import path from 'node:path';

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    dts({
      include: ['src/components/ui/**/*.{ts,tsx}'],
      outDir: 'dist-lib',
      // Per-file .d.ts tree, not a single rolled-up file: the design-sync
      // converter walks the real .d.ts tree, and rollupTypes needs
      // @microsoft/api-extractor, an extra heavy dependency this doesn't need.
      entryRoot: 'src/components/ui',
      // tsconfig.json is a solution-style file (just project references,
      // no compilerOptions of its own) — point at the real one, and
      // override its noEmit:true (correct for the app's own tsc -b check,
      // fatal for a plugin whose whole job is emitting .d.ts files).
      tsconfigPath: 'tsconfig.app.json',
      compilerOptions: { noEmit: false },
    }),
  ],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  // Nothing under public/ (favicons, the app's own static assets) belongs
  // in a component library's dist output.
  publicDir: false,
  build: {
    outDir: 'dist-lib',
    emptyOutDir: true,
    lib: {
      entry: path.resolve(__dirname, 'src/components/ui/index.ts'),
      formats: ['es'],
      fileName: () => 'index.js',
    },
    rollupOptions: {
      external: ['react', 'react-dom', 'react/jsx-runtime'],
    },
  },
});
