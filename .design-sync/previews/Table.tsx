import {
  Table,
  TableHeader,
  TableBody,
  TableRow,
  TableHead,
  TableCell,
  Badge,
} from 'web';

const rows = [
  { name: 'Ada Receiver', code: 'EMP00412', type: 'operator', status: 'Active', role: 'RECEIVER@TKY' },
  { name: 'Kenji Tanaka', code: 'EMP00298', type: 'staff', status: 'Active', role: 'WAREHOUSE_MANAGER@TKY' },
  { name: 'Yuki Sato', code: 'EMP00517', type: 'operator', status: 'Suspended', role: 'PUTAWAY_OPERATOR@TKY' },
];

export function Default() {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>Name</TableHead>
          <TableHead>Employee code</TableHead>
          <TableHead>Type</TableHead>
          <TableHead>Status</TableHead>
          <TableHead>Role scopes</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map((row) => (
          <TableRow key={row.code}>
            <TableCell>{row.name}</TableCell>
            <TableCell>{row.code}</TableCell>
            <TableCell>{row.type}</TableCell>
            <TableCell>
              <Badge variant={row.status === 'Active' ? 'default' : 'destructive'}>
                {row.status}
              </Badge>
            </TableCell>
            <TableCell>{row.role}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
