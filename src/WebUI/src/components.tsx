export function Status({ value }: { value: string }) {
  const tone = ['completed', 'approved', 'ok'].includes(value.toLowerCase()) ? 'good'
    : ['failed', 'rejected', 'cancelled'].includes(value.toLowerCase()) ? 'bad'
    : ['executing', 'inprogress', 'retrying'].includes(value.toLowerCase()) ? 'active' : 'neutral';
  return <span className={`status ${tone}`}><span aria-hidden="true">●</span> {value}</span>;
}
