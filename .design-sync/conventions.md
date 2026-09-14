## Using this design system

This is the WMS admin tool's component library — shadcn/ui primitives on
Radix + Tailwind CSS v4, for an internal warehouse-operations admin
surface (user/role management, master data, reporting). The visual
direction is **clean and utilitarian**: neutral grayscale, one blue accent
reserved for interactive/primary-action meaning, high contrast for dense
scanning by someone using this all day. Not a consumer/marketing product —
avoid decorative color, gradients, or playful copy.

### Setup

No React provider wrapper is required — colors resolve from plain CSS
custom properties (`:root` / `.dark`), not React context. The only
requirement is that `styles.css` is loaded on the page; every component's
classes resolve against it directly.

### Styling idiom: Tailwind utility classes bound to CSS custom properties

Never invent a new color. Every surface/text/border pairing below is a
real, verified utility class in this bundle:

| Surface | Background | Text |
|---|---|---|
| Page | `bg-background` | `text-foreground` |
| Card | `bg-card` | `text-card-foreground` |
| Popover / dropdown / dialog | `bg-popover` | `text-popover-foreground` |
| Primary action | `bg-primary` | `text-primary` (on-light) / `text-primary-foreground` (on-fill) |
| Secondary | `bg-secondary` | `text-secondary-foreground` |
| Muted / de-emphasized | `bg-muted` (implied) | `text-muted-foreground` |
| Destructive | `bg-destructive` (implied) | `text-destructive` |

Borders: `border-border` (default), `border-input` (form controls),
`border-destructive` (invalid state). Radius: `--radius` (0.5rem base) and
`--radius-md` are real custom tokens or components reference directly
(`rounded-[min(var(--radius-md),10px)]`); everything else uses Tailwind's
ordinary `rounded-md`/`rounded-lg`/`rounded-xl` scale, not a custom token.

Both a light and a dark palette are defined (`:root` / `.dark`) — every
component already carries `dark:` variants, so building dark-mode-aware
compositions works out of the box even though the app doesn't have a
theme toggle wired up yet.

### Where the truth lives

`styles.css` (this bundle's compiled stylesheet) is the resolved theme —
read it before styling anything by hand. Each component's own
`<Name>.d.ts` is its real prop contract; `<Name>.prompt.md` has composition
examples.

### A real composition

```tsx
<Card>
  <CardHeader>
    <CardTitle>Ada Receiver</CardTitle>
    <CardDescription>EMP00412 · Tokyo DC1</CardDescription>
    <CardAction>
      <Badge variant="default">Active</Badge>
    </CardAction>
  </CardHeader>
  <CardContent>
    <p>Role scopes: RECEIVER @ TKY</p>
  </CardContent>
  <CardFooter>
    <Button variant="outline" size="sm">Edit</Button>
    <Button size="sm">Assign role</Button>
  </CardFooter>
</Card>
```
