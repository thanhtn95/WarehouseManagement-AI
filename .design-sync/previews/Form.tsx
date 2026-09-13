import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import {
  Form,
  FormField,
  FormItem,
  FormLabel,
  FormControl,
  FormDescription,
  FormMessage,
  Input,
} from 'web';

interface Values {
  employeeCode: string;
}

export function Default() {
  const form = useForm<Values>({ defaultValues: { employeeCode: '' } });

  return (
    <Form {...form}>
      <form style={{ width: 280 }}>
        <FormField
          control={form.control}
          name="employeeCode"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Employee code</FormLabel>
              <FormControl>
                <Input placeholder="EMP00412" {...field} />
              </FormControl>
              <FormDescription>Matches the badge printed on the handheld.</FormDescription>
              <FormMessage />
            </FormItem>
          )}
        />
      </form>
    </Form>
  );
}

// Exercises FormMessage's real error-rendering path — a validation error
// set through react-hook-form's own API, not a hand-styled lookalike.
export function ValidationError() {
  const form = useForm<Values>({ defaultValues: { employeeCode: '' } });

  useEffect(() => {
    form.setError('employeeCode', {
      type: 'required',
      message: 'Employee code is required.',
    });
  }, [form]);

  return (
    <Form {...form}>
      <form style={{ width: 280 }}>
        <FormField
          control={form.control}
          name="employeeCode"
          render={({ field }) => (
            <FormItem>
              <FormLabel>Employee code</FormLabel>
              <FormControl>
                <Input placeholder="EMP00412" {...field} />
              </FormControl>
              <FormMessage />
            </FormItem>
          )}
        />
      </form>
    </Form>
  );
}
