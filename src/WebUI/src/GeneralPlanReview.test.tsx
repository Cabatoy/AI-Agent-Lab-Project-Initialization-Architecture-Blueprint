import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { api, ApiError, type GeneralPlan } from './api';
import { GeneralPlanReview } from './GeneralPlanReview';

vi.mock('./api', async importOriginal => ({
  ...await importOriginal<typeof import('./api')>(),
  api: { generalPlan: vi.fn(), generateGeneralPlan: vi.fn(), reviseGeneralPlan: vi.fn(), approveGeneralPlan: vi.fn() },
}));
const id = '11234567-89ab-cdef-0123-456789abcdef';
const plan: GeneralPlan = {
  revision: 1, source: 'model', createdAtUtc: new Date().toISOString(),
  tasks: [{ title: 'Design', description: 'Review design' }], approved: false, rejected: false, taskIds: [],
};

beforeEach(() => {
  vi.mocked(api.generalPlan).mockRejectedValue(new ApiError('http', 'Not found', 404));
  vi.mocked(api.generateGeneralPlan).mockResolvedValue(plan);
  vi.mocked(api.reviseGeneralPlan).mockResolvedValue({
    ...plan, revision: 2, source: 'human', tasks: [{ title: 'Reviewed', description: 'Review design' }],
  });
  vi.mocked(api.approveGeneralPlan).mockResolvedValue({
    ...plan, revision: 2, source: 'human', tasks: [{ title: 'Reviewed', description: 'Review design' }],
    approved: true, taskIds: [id],
  });
});
afterEach(() => { cleanup(); vi.resetAllMocks(); });

it('requires saved edits and explicit review before creating Draft tasks', async () => {
  const created = vi.fn();
  const view = vi.fn();
  render(<GeneralPlanReview conversationId={id} token="cookie-session" hasMessages
    onTasksCreated={created} onViewTask={view} />);
  await screen.findByText('No model plan yet.');
  fireEvent.click(screen.getByRole('button', { name: 'Generate model plan' }));
  expect(await screen.findByText('Revision 1 · model · Not approved')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Approve Draft task creation' })).toBeDisabled();
  fireEvent.change(screen.getByLabelText('Task 1 title'), { target: { value: 'Reviewed' } });
  expect(screen.getByLabelText('I reviewed this exact saved revision.')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save edited revision' }));
  await screen.findByText('Revision 2 · human · Not approved');
  fireEvent.click(screen.getByLabelText('I reviewed this exact saved revision.'));
  fireEvent.click(screen.getByRole('button', { name: 'Approve Draft task creation' }));
  await screen.findByText('Revision 2 · human · Draft tasks created');
  expect(api.approveGeneralPlan).toHaveBeenCalledWith(id, 2, 'cookie-session');
  expect(created).toHaveBeenCalledOnce();
  fireEvent.click(screen.getByRole('button', { name: /Open Draft task/ }));
  expect(view).toHaveBeenCalledWith(id);
});
