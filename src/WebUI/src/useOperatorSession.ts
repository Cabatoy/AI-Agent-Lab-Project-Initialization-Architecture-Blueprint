import { useEffect, useState } from 'react';
import { api, COOKIE_SESSION, OPERATOR_SESSION_ENDED, type LocalRunnerSession } from './api';

export function useOperatorSession(
  restore: (token: string, active: () => boolean) => Promise<void>,
  clear: () => void,
  onError: (reason: unknown) => void,
) {
  const [restoring, setRestoring] = useState(true);
  const [notice, setNotice] = useState('');
  const [expiry, setExpiry] = useState<number | null>(null);
  useEffect(() => {
    let active = true;
    let invalidated = false;
    const ended = () => {
      invalidated = true;
      setRestoring(false);
      clear(); setExpiry(null);
      setNotice('Operator session expired or was revoked. Reauthenticate explicitly; no write was automatically retried.');
    };
    window.addEventListener(OPERATOR_SESSION_ENDED, ended);
    void api.restoreLocalRunnerSession().then(async session => {
      if (!active || invalidated) return;
      await restore(COOKIE_SESSION, () => active && !invalidated);
      if (active && !invalidated) { setExpiry(Date.parse(session.expiresAtUtc)); setNotice(''); }
    }).catch(reason => {
      if (active && !invalidated && reason?.status !== 401) onError(reason);
    }).finally(() => { if (active) setRestoring(false); });
    return () => { active = false; window.removeEventListener(OPERATOR_SESSION_ENDED, ended); };
  }, []);
  useEffect(() => {
    if (expiry === null) return;
    const timer = setTimeout(() => {
      clear(); setExpiry(null);
      setNotice('Operator session expired. Reauthenticate explicitly; existing approvals were not changed.');
    }, Math.max(0, expiry - Date.now()));
    return () => clearTimeout(timer);
  }, [expiry]);
  return {
    restoring, notice,
    authenticated(session: LocalRunnerSession) {
      setExpiry(session.expiresAtUtc ? Date.parse(session.expiresAtUtc) : Date.now() + session.expiresInSeconds * 1000);
      setNotice('');
    },
    async logout(token: string) {
      try { await api.revokeLocalRunnerSession(token); }
      catch (reason) {
        if (!(reason && typeof reason === 'object' && 'status' in reason && reason.status === 401)) throw reason;
      }
      clear(); setExpiry(null);
      setNotice('Signed out. Reauthenticate explicitly to continue.');
    },
  };
}
