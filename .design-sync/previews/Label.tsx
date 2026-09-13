import { Label, Input } from 'web';

export function Default() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4, width: 240 }}>
      <Label htmlFor="preview-display-name">Display name</Label>
      <Input id="preview-display-name" defaultValue="Ada Receiver" />
    </div>
  );
}
