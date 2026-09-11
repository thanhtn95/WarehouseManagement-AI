import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';

function Hello() {
  return <p>WMS admin</p>;
}

describe('test harness', () => {
  it('renders', () => {
    render(<Hello />);
    expect(screen.getByText('WMS admin')).toBeInTheDocument();
  });
});
