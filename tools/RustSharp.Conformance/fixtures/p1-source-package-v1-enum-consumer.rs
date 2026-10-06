use SourceProducer::{Value, make, read};
fn main() {
    let value: Value = make();
    println!("{}", read(value));
    println!("{}", read(Value::Named { value: 42 }));
    println!("{}", read(Value::Empty));
}
