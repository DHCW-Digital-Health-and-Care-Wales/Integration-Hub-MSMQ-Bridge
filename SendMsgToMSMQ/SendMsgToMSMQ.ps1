# Define the queue path
$queuePath = ".\private$\WASPQueue1"

# Check if the queue exists, and create it if it doesn't
if (-not [System.Messaging.MessageQueue]::Exists($queuePath)) {
    [System.Messaging.MessageQueue]::Create($queuePath)
    Write-Host "Queue created successfully."
} else {
    Write-Host "Queue already exists."
}

# Create an XML payload
$xmlPayload = @"
<message>
    <subject>Test Message</subject>
    <body>Hello, this is a test message in XML format!</body>
</message>
"@

# Create the message and set its label and body to the XML content
$message = New-Object System.Messaging.Message
$message.Label = "Test XML Message"
$message.Body = $xmlPayload  # Set XML payload as the message body

# Create the queue object and send the message
$queue = New-Object System.Messaging.MessageQueue $queuePath
$queue.Send($message)

# Get the message count in the queue
$messageCount = $queue.GetMessageEnumerator2().Count
Write-Host "Message count: $messageCount"
