import {
  Select,
  SelectTrigger,
  SelectValue,
  SelectContent,
  SelectItem,
  SelectGroup,
  SelectLabel,
} from 'web';

// Rendered forced-open (defaultOpen) — a static preview of the closed
// trigger alone would show almost nothing about how this component works.
export function Default() {
  return (
    <Select defaultOpen defaultValue="RECEIVER">
      <SelectTrigger style={{ width: 220 }}>
        <SelectValue placeholder="Select a role" />
      </SelectTrigger>
      <SelectContent>
        <SelectGroup>
          <SelectLabel>Roles</SelectLabel>
          <SelectItem value="RECEIVER">Receiver</SelectItem>
          <SelectItem value="PUTAWAY_OPERATOR">Putaway Operator</SelectItem>
          <SelectItem value="SUPERVISOR">Supervisor</SelectItem>
          <SelectItem value="AUDITOR">Auditor</SelectItem>
        </SelectGroup>
      </SelectContent>
    </Select>
  );
}
