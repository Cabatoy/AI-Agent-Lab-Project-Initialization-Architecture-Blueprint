import { DESCRIPTION_MAX_LENGTH, TITLE_MAX_LENGTH } from './limits';

// A single unsent text buffer, not a Control Plane Task or execution queue.
// Browser-profile storage only: no auto-POST, sync, model call or task authority.
export const UNSENT_DRAFT_KEY = 'hic.command-center.unsent.v1';

export interface UnsentDraft {
  title: string;
  description: string;
  /** True when an earlier POST outcome was ambiguous; blocks blind retry even after reload. */
  uncertain: boolean;
}

const EMPTY: UnsentDraft = { title: '', description: '', uncertain: false };
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

export function readUnsentDraft(): UnsentDraft {
  if (typeof window === 'undefined') return { ...EMPTY };
  try {
    const raw = window.localStorage.getItem(UNSENT_DRAFT_KEY);
    if (!raw) return { ...EMPTY };
    const value: unknown = JSON.parse(raw);
    if (!isObject(value) || value.version !== 1 || typeof value.title !== 'string' ||
      typeof value.description !== 'string' || typeof value.uncertain !== 'boolean' ||
      value.title.length > TITLE_MAX_LENGTH || value.description.length > DESCRIPTION_MAX_LENGTH) {
      return { ...EMPTY };
    }
    return { title: value.title, description: value.description, uncertain: value.uncertain };
  } catch {
    // Storage may be disabled, unavailable, or contain malformed data. Do not leak it.
    return { ...EMPTY };
  }
}

export function saveUnsentDraft(draft: UnsentDraft): boolean {
  if (typeof window === 'undefined' || draft.title.length > TITLE_MAX_LENGTH ||
    draft.description.length > DESCRIPTION_MAX_LENGTH) return false;
  try {
    if (!draft.title && !draft.description && !draft.uncertain) {
      window.localStorage.removeItem(UNSENT_DRAFT_KEY);
    } else {
      window.localStorage.setItem(UNSENT_DRAFT_KEY, JSON.stringify({ version: 1, ...draft }));
    }
    return true;
  } catch {
    return false;
  }
}
