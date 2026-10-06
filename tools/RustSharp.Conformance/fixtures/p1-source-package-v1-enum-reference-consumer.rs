use SourceProducer::{none, some, read};
fn main() {
    println!("{}", read(none()));
    println!("{}", read(some()));
}
