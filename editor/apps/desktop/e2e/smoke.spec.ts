import { expect, test } from './fixtures';

test('starts the engine and connects the editor to it', async ({ window }) => {
  await expect(window.getByTestId('engine-status')).toHaveAttribute('data-state', /connected|inprocess/);
  // A fresh Server has no project open: the editor offers to create or open one.
  await expect(window.getByRole('region', { name: 'Projects' })).toBeVisible();
});
