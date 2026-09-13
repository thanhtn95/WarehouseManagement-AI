// Library entry point for the ui component set — one place that re-exports
// every primitive, so a build tool (or design-sync) has a single, real
// module to point at instead of ten separate files.
//
// The theme import is deliberate: it's what makes the library build (see
// vite.lib.config.ts) emit a real, compiled stylesheet with the actual
// Tailwind utility classes these components use and the resolved theme
// variables — not just re-exported component code with no styling.
import '../../index.css';

export { Badge, badgeVariants } from './badge';
export { Button, buttonVariants } from './button';
export {
  Card,
  CardHeader,
  CardFooter,
  CardTitle,
  CardAction,
  CardDescription,
  CardContent,
} from './card';
export {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogOverlay,
  DialogPortal,
  DialogTitle,
  DialogTrigger,
} from './dialog';
export {
  useFormField,
  Form,
  FormItem,
  FormLabel,
  FormControl,
  FormDescription,
  FormMessage,
  FormField,
} from './form';
export { Input } from './input';
export { Label } from './label';
export {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectScrollDownButton,
  SelectScrollUpButton,
  SelectSeparator,
  SelectTrigger,
  SelectValue,
} from './select';
export { Toaster } from './sonner';
export {
  Table,
  TableHeader,
  TableBody,
  TableFooter,
  TableHead,
  TableRow,
  TableCell,
  TableCaption,
} from './table';
