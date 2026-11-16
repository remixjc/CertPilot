import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
export default defineConfig({ plugins: [vue()], build: { outDir: '../src/CertPilot.Service/wwwroot', emptyOutDir: true }, server: { proxy: { '/api': 'http://127.0.0.1:8080', '/openapi': 'http://127.0.0.1:8080', '/health': 'http://127.0.0.1:8080' } } });
