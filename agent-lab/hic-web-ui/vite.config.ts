import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { localApiProxy } from './vite.proxy';
import { localNativeProxy } from './vite.native.proxy';

export default defineConfig({
  plugins: [react(), localNativeProxy(), localApiProxy()],
  server: { host: '127.0.0.1', port: 5173, strictPort: true, cors: false },
  test: { environment: 'jsdom', setupFiles: ['./src/test-setup.ts'], restoreMocks: true },
});
