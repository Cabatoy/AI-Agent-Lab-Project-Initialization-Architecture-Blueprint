export const TITLE_MAX_LENGTH = 200;
export const DESCRIPTION_MAX_LENGTH = 8000;
export const DERIVED_TITLE_LENGTH = 80;
// Upper bound for the serialized JSON body accepted by the local proxy.
export const MAX_REQUEST_BYTES = 64 * 1024;
export const COMMAND_CENTER_HEADER = 'X-HIC-Request';
export const COMMAND_CENTER_HEADER_VALUE = 'command-center';

export interface CreateTaskInput { title: string; description: string }

export type CreateTaskValidation = { ok: true; value: CreateTaskInput } | { ok: false; error: string };

export function validateCreateTask(value: unknown): CreateTaskValidation {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return { ok: false, error: 'Request body must be a JSON object.' };
  const record = value as Record<string, unknown>;
  if (Object.keys(record).some(key => key !== 'title' && key !== 'description')) {
    return { ok: false, error: 'Only title and description are accepted.' };
  }
  const { title, description } = record;
  if (typeof title !== 'string' || !title.trim()) return { ok: false, error: 'Title is required.' };
  if (title.length > TITLE_MAX_LENGTH) return { ok: false, error: `Title must be at most ${TITLE_MAX_LENGTH} characters.` };
  if (typeof description !== 'string' || !description.trim()) return { ok: false, error: 'Request description is required.' };
  if (description.length > DESCRIPTION_MAX_LENGTH) {
    return { ok: false, error: `Request must be at most ${DESCRIPTION_MAX_LENGTH} characters.` };
  }
  return { ok: true, value: { title, description } };
}

export function deriveTitle(request: string): string {
  const firstLine = request.split(/\r?\n/).map(line => line.replace(/\s+/g, ' ').trim()).find(Boolean) ?? '';
  if (firstLine.length <= DERIVED_TITLE_LENGTH) return firstLine;
  const cut = firstLine.slice(0, DERIVED_TITLE_LENGTH - 1);
  const boundary = cut.lastIndexOf(' ');
  return `${(boundary >= DERIVED_TITLE_LENGTH / 2 ? cut.slice(0, boundary) : cut).trimEnd()}…`;
}
