import { Badge } from 'web';

export function Variants() {
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
      <Badge variant="default">Active</Badge>
      <Badge variant="secondary">Draft</Badge>
      <Badge variant="destructive">Suspended</Badge>
      <Badge variant="outline">Pending</Badge>
      <Badge variant="ghost">Archived</Badge>
      <Badge variant="link">View details</Badge>
    </div>
  );
}
