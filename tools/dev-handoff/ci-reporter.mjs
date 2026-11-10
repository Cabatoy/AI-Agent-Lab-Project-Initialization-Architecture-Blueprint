export default async function* reporter(events) {
  for await (const event of events) {
    if (event.type === 'test:fail') {
      const { name, details } = event.data;
      const error = details?.error?.cause ?? details?.error;
      const text = `${name}\n${error?.stack ?? error?.message ?? 'Test failed'}`.slice(0, 4000);
      yield `::error::${text.replaceAll('%', '%25').replaceAll('\r', '%0D').replaceAll('\n', '%0A')}\n`;
    }
    if (event.type === 'test:summary') {
      yield `Offline test summary: ${JSON.stringify(event.data.counts)}; success=${event.data.success}\n`;
    }
  }
}
