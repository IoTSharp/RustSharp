use SourceProducer::{make, project};

fn main() {
    let first = project(make());
    println!("{}", first.value);
}
