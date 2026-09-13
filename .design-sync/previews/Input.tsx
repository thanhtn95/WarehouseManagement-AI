import { Input } from 'web';

export function States() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, width: 280 }}>
      <Input defaultValue="Ada Receiver" />
      <Input placeholder="Employee code" />
      <Input defaultValue="ada@example.com" disabled />
      <Input defaultValue="" aria-invalid placeholder="Warehouse ID" />
    </div>
  );
}
