export const environment = {
  production: false,
  appVersion: 'V1.2',
  apiBaseUrl: '/api',
  ticketAutomation: {
    pollingEnabled: true,
    pollIntervalSeconds: 30,
    closedStatusName: 'Closed',
    newStatusName: 'New',
    sourceDatabaseName: 'PoolProd',
    similaritySearchLimit: 5,
    email: {
      deliveryMode: 'DatabaseMail',
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