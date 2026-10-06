pub enum Value { Empty, Number(i32), Named { value: i32 } }
pub fn make() -> Value { Value::Number(40) }
pub fn read(value: Value) -> i32 {
    match value {
        Value::Empty => 0,
        Value::Number(n) => n,
        Value::Named { value: n } => n
    }
}
fn main() {}
