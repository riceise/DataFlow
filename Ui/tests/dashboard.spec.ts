import { test, expect } from '@playwright/test'

test('overview keeps technical IDs out of the ledger and adapts without page overflow', async ({
  page,
}, testInfo) => {
  const sourceId = '971fbdd8-aa18-4121-8cad-8cf4ee18fe23'
  const jobId = 'dd78b18c-7d9d-4c93-843f-bb77bfae0068'
  const recentJobs = ['Completed', 'Processing', 'CompletedWithErrors', 'Failed'].map(
    (status, index) => ({
      id: `${jobId.slice(0, -1)}${index}`,
      dataSourceId: sourceId,
      dataSourceName: 'Клиенты интернет-магазина',
      originalFileName:
        index === 0
          ? 'clients-november-final-corrected-export-2026.csv'
          : `customers-${index}.xlsx`,
      status,
      createdAt: '2026-11-10T12:32:00Z',
      processedRows: 12500,
      totalRows: status === 'Processing' ? null : 12500,
      invalidRows: ['Failed', 'CompletedWithErrors'].includes(status) ? 150 : 0,
    }),
  )
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname.replace('/api/v1', '')
    if (path === '/auth/me')
      return route.fulfill({ json: { id: 'user', email: 'operator@example.test' } })
    if (path === '/dashboard/summary')
      return route.fulfill({
        json: {
          totalJobs: 24,
          completedJobs: 17,
          completedWithErrorsJobs: 3,
          failedJobs: 1,
          attentionJobs: 4,
          activeJobs: 2,
          processedRows: 50000,
          insertedRows: 36000,
          updatedRows: 9000,
          invalidRows: 3000,
          duplicateRows: 2000,
          validRows: 0,
          recentJobs,
        },
      })
    return route.fulfill({ status: 404, json: {} })
  })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Последние импорты' })).toBeVisible()
  await expect(page.locator('.import-table')).not.toContainText(jobId.slice(0, -1))
  await expect(page.locator('.import-table')).not.toContainText(sourceId)
  await expect(page.getByRole('link', { name: 'customers-2.xlsx', exact: true })).toHaveAttribute(
    'href',
    `/imports/${recentJobs[2]!.id}`,
  )
  await expect(page.getByRole('link', { name: /Ошибки строк: 150, customers-2/ })).toHaveAttribute(
    'href',
    `/imports/${recentJobs[2]!.id}?rows=Invalid`,
  )

  const widths = testInfo.project.name === 'desktop' ? [1440, 1024, 768] : [390, 360]
  for (const width of widths) {
    await page.setViewportSize({ width, height: 1000 })
    for (const theme of ['light', 'dark']) {
      if (width < 1024) await page.getByRole('button', { name: 'Меню', exact: true }).click()
      await page
        .getByRole('radio', { name: theme === 'light' ? 'Светлая' : 'Тёмная', exact: true })
        .check()
      if (width < 1024) {
        await page.keyboard.press('Escape')
        await expect(page.getByRole('button', { name: 'Меню', exact: true })).toBeFocused()
      }
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
      ).toBe(true)
      await page.screenshot({
        path: testInfo.outputPath(`overview-${width}-${theme}.png`),
        fullPage: true,
      })
    }
  }
})
