import {
  Card,
  CardHeader,
  CardTitle,
  CardDescription,
  CardAction,
  CardContent,
  CardFooter,
  Button,
  Badge,
} from 'web';

export function Default() {
  return (
    <Card style={{ width: 360 }}>
      <CardHeader>
        <CardTitle>Ada Receiver</CardTitle>
        <CardDescription>EMP00412 · Tokyo DC1</CardDescription>
        <CardAction>
          <Badge variant="default">Active</Badge>
        </CardAction>
      </CardHeader>
      <CardContent>
        <p style={{ margin: 0 }}>Role scopes: RECEIVER @ TKY</p>
        <p style={{ margin: '4px 0 0', color: 'var(--muted-foreground)' }}>
          Valid until 2026-12-31
        </p>
      </CardContent>
      <CardFooter style={{ gap: 8 }}>
        <Button variant="outline" size="sm">
          Edit
        </Button>
        <Button size="sm">Assign role</Button>
      </CardFooter>
    </Card>
  );
}
