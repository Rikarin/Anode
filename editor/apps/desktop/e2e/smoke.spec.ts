import { expect, test } from './fixtures';

test('starts the engine and connects the editor to it', async ({ window }) => {
  await expect(window.getByTestId('engine-status')).toHaveAttribute('data-state', /connected|inprocess/);
  await expect(window.getByRole('region', { name: 'Tracks' })).toBeVisible();
});
