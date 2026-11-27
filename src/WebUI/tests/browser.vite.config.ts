import { readFileSync } from 'node:fs';
import { defineConfig, mergeConfig } from 'vite';
import appConfig from '../vite.config';

const cert = process.env.HIC_BROWSER_TEST_CERT;
const key = process.env.HIC_BROWSER_TEST_KEY;
if (!cert || !key) throw new Error('Browser test TLS certificate and key are required.');

export default mergeConfig(appConfig, defineConfig({
  logLevel: 'silent',
  server: {
    https: { cert: readFileSync(cert), key: readFileSync(key) },
    hmr: false,
  },
}));
