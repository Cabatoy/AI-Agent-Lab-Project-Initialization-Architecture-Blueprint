export function hicWebPort(value = process.env.HIC_WEB_PORT): number {
  if (value === undefined || value === '') return 19482;
  if (!/^[0-9]{4,5}$/.test(value)) throw new Error('HIC_WEB_PORT must be a port from 1024 to 65535.');
  const port = Number(value);
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    throw new Error('HIC_WEB_PORT must be a port from 1024 to 65535.');
  }
  return port;
}
