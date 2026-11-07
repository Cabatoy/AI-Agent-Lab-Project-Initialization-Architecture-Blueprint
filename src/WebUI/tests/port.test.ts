import { describe, expect, it } from 'vitest';
import { hicWebPort } from '../vite.port';
import { apiTarget } from '../vite.proxy';
import { nativeTarget } from '../vite.native.proxy';

describe('HIC per-machine port selection', () => {
  it('uses the shared host port defaults for new installations', () => {
    expect(hicWebPort()).toBe(19482);
    expect(apiTarget().port).toBe('19480');
    expect(nativeTarget().port).toBe('19481');
  });
  it('accepts explicit legacy and custom UI ports', () => {
    expect(hicWebPort('5180')).toBe(5180);
    expect(hicWebPort('20555')).toBe(20555);
  });
  it.each(['0', '80', '65536', '-1', '1234/extra', 'abc'])('rejects invalid ports %s', value => {
    expect(() => hicWebPort(value)).toThrow();
  });
});
