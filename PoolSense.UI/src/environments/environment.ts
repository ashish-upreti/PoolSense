export const environment = {
  production: true,
  appVersion: 'v1.7',
  apiBaseUrl: '/api',
  ticketAutomation: {
    pollingEnabled: true,
    pollIntervalSeconds: 30,
    poolSenseEmail: true,
    closedStatusName: 'Closed',
    newStatusName: 'New',
    sourceDatabaseName: 'PoolProd',
    similaritySearchLimit: 5,
    email: {
      deliveryMode: 'Smtp',
      fromAddress: 'PoolSense@intel.com',
      smtpHost: 'smtp.intel.com',
      port: 25,
      timeoutMs: 30000,
      databaseMailProfile: 'PoolSense@intel.com',
    },
  },
  projectDefaults: {
    knowledgeLookbackYears: 2,
    similaritySearchLimit: 5,
    sendEmail: true,
    poolingEnabled: true,
    emailRecipients: '',
  },
}